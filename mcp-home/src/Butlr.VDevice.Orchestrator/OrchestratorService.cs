using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Core.Capabilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CoreVDevice = Butlr.VDevice.Core.VDevice;

namespace Butlr.VDevice.Orchestrator;

public sealed record WinnerChangedEvent(
    string DeviceId,
    ArbitrationResult? Previous,
    ArbitrationResult? Current,
    DateTimeOffset At);

public sealed class OrchestratorService : BackgroundService
{
    private readonly TierRegistry _registry;
    private readonly IDriver _driver;
    private readonly ILogger<OrchestratorService> _logger;
    private readonly ObservabilityService _observability;

    private readonly ConcurrentDictionary<VDeviceId, CoreVDevice> _vdevices = new();
    private readonly ConcurrentDictionary<string, ArbitrationResult?> _lastWinners = new();
    private readonly Subject<WinnerChangedEvent> _winnerChanges = new();

    public IObservable<WinnerChangedEvent> WinnerChanges => _winnerChanges;

    // Channel d'événements par app_id pour les préemptions
    private readonly ConcurrentDictionary<string, Subject<VDeviceEvent>> _appChannels = new();

    public OrchestratorService(
        TierRegistry registry,
        IDriver driver,
        ObservabilityService observability,
        ILogger<OrchestratorService> logger)
    {
        _registry = registry;
        _driver = driver;
        _observability = observability;
        _logger = logger;
    }

    public async Task<CoreVDevice> CreateVDeviceAsync(
        string deviceId, string actorKind, string tierId, int priority,
        ClusterId cluster, AttributeId attribute, object value,
        VDeviceDuration duration,
        string? appId = null, string? actorUserId = null, string? viaAgentId = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var vdevice = CoreVDevice.Create(deviceId, actorKind, tierId, priority,
            cluster, attribute, value, duration, _registry, now,
            appId, actorUserId, viaAgentId);

        _vdevices[vdevice.Id] = vdevice;
        _logger.LogInformation("VDevice {VDeviceId} created on device {DeviceId} tier {TierId}",
            vdevice.Id, deviceId, tierId);
        _observability.OnVDeviceCreated(vdevice);

        await ResolveAndApplyAsync(deviceId, ct);
        return vdevice;
    }

    public async Task<bool> RenewVDeviceAsync(VDeviceId id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_vdevices.TryGetValue(id, out var vdevice)) return false;

        try
        {
            var renewed = VDeviceLifecycle.Renew(vdevice, now);
            _vdevices[id] = renewed;
            _observability.OnVDeviceRenewed(renewed);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Renew hors-fenêtre pour VDevice {VDeviceId}", id);
            return false;
        }
    }

    public async Task<CoreVDevice?> UpdateVDeviceAsync(VDeviceId id, object newValue, CancellationToken ct = default)
    {
        if (!_vdevices.TryGetValue(id, out var vdevice)) return null;

        var now = DateTimeOffset.UtcNow;
        var updated = vdevice with { Value = newValue, LastRenewAt = now };
        _vdevices[id] = updated;

        await ResolveAndApplyAsync(vdevice.DeviceId, ct);
        return updated;
    }

    public async Task ReleaseVDeviceAsync(VDeviceId id, CancellationToken ct = default)
    {
        if (!_vdevices.TryRemove(id, out var vdevice)) return;

        _logger.LogInformation("VDevice {VDeviceId} released", id);
        _observability.OnVDeviceReleased(vdevice);

        await ResolveAndApplyAsync(vdevice.DeviceId, ct);
    }

    public IReadOnlyList<CoreVDevice> GetActiveByDevice(string deviceId)
        => _vdevices.Values
            .Where(v => string.Equals(v.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IObservable<VDeviceEvent> SubscribeApp(string appId)
        => _appChannels.GetOrAdd(appId, _ => new Subject<VDeviceEvent>());

    private async Task ResolveAndApplyAsync(string deviceId, CancellationToken ct)
    {
        var active = GetActiveByDevice(deviceId);
        var result = Arbitration.Resolve(active, _registry);
        _observability.OnArbitration(deviceId, active.Count, result);

        var prev = _lastWinners.TryGetValue(deviceId, out var p) ? p : null;
        if (HasChanged(prev, result))
        {
            _lastWinners[deviceId] = result;
            _winnerChanges.OnNext(new WinnerChangedEvent(deviceId, prev, result, DateTimeOffset.UtcNow));
            NotifyPreempted(prev, result, active);
            await _driver.ApplyCommandAsync(deviceId, result, ct);
        }
    }

    private static bool HasChanged(ArbitrationResult? a, ArbitrationResult? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        return !Equals(a.Value, b.Value) || a.WinningVDeviceId != b.WinningVDeviceId;
    }

    private void NotifyPreempted(ArbitrationResult? prev, ArbitrationResult? current, IReadOnlyList<CoreVDevice> active)
    {
        if (prev is null) return;

        var prevVDevice = active.FirstOrDefault(v => v.Id == prev.WinningVDeviceId);
        if (prevVDevice?.AppId is not null && current?.WinningVDeviceId != prev.WinningVDeviceId)
        {
            var subject = _appChannels.GetOrAdd(prevVDevice.AppId, _ => new Subject<VDeviceEvent>());
            subject.OnNext(new VDevicePreemptedEvent(prevVDevice.Id, prevVDevice.DeviceId, "higher_priority", DateTimeOffset.UtcNow));
            _observability.OnVDevicePreempted(prevVDevice, "higher_priority");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VDeviceLifecycle.HeartbeatIntervalMs / 2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeExpiredAsync(stoppingToken);
        }
    }

    private async Task PurgeExpiredAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = VDeviceLifecycle.Tick(_vdevices.Values, now);

        foreach (var id in expired)
        {
            if (!_vdevices.TryRemove(id, out var vdevice)) continue;

            _logger.LogInformation("VDevice {VDeviceId} expiré sur device {DeviceId}", id, vdevice.DeviceId);
            _observability.OnVDeviceExpired(vdevice);

            if (vdevice.AppId is not null)
            {
                var subject = _appChannels.GetOrAdd(vdevice.AppId, _ => new Subject<VDeviceEvent>());
                subject.OnNext(new VDeviceExpiredEvent(vdevice.Id, vdevice.DeviceId, DateTimeOffset.UtcNow));
            }

            await ResolveAndApplyAsync(vdevice.DeviceId, ct);
        }
    }
}
