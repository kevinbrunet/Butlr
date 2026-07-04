using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Butlr.VDevice.Core;
using Microsoft.Extensions.Logging;
using CoreVDevice = Butlr.VDevice.Core.VDevice;

namespace Butlr.VDevice.Orchestrator;

public sealed class ObservabilityService
{
    private static readonly ActivitySource ActivitySource = new("Butlr.VDevice");
    private static readonly Meter Meter = new("Butlr.VDevice");

    private readonly ObservableGauge<int> _activeGauge;
    private readonly Histogram<double> _arbitrationDuration;

    private readonly ILogger<ObservabilityService> _logger;

    // Cache "recent activity" — ~ 1000 dernières décisions par device (cf. ADR 0016)
    private readonly ConcurrentDictionary<string, Queue<ArbitrationRecord>> _recentActivity = new();
    private const int MaxRecentPerDevice = 1000;

    public ObservabilityService(ILogger<ObservabilityService> logger)
    {
        _logger = logger;
        _activeGauge = Meter.CreateObservableGauge("butlr.vdevice.active",
            () => 0, "count", "Nombre de VDevices actifs");
        _arbitrationDuration = Meter.CreateHistogram<double>(
            "butlr.arbitration.duration", "ms", "Durée d'arbitrage par device");
    }

    public void OnVDeviceCreated(CoreVDevice vdevice)
        => _logger.LogInformation(
            "vdevice.created {VDeviceId} device={DeviceId} tier={TierId} actor={ActorKind} priority={Priority}",
            vdevice.Id, vdevice.DeviceId, vdevice.TierId, vdevice.ActorKind, vdevice.Priority);

    public void OnVDeviceRenewed(CoreVDevice vdevice)
        => _logger.LogInformation("vdevice.renewed {VDeviceId}", vdevice.Id);

    public void OnVDeviceReleased(CoreVDevice vdevice)
        => _logger.LogInformation("vdevice.released {VDeviceId} device={DeviceId}", vdevice.Id, vdevice.DeviceId);

    public void OnVDeviceExpired(CoreVDevice vdevice)
        => _logger.LogInformation("vdevice.expired {VDeviceId} device={DeviceId}", vdevice.Id, vdevice.DeviceId);

    public void OnVDevicePreempted(CoreVDevice vdevice, string reason)
        => _logger.LogInformation("vdevice.preempted {VDeviceId} reason={Reason}", vdevice.Id, reason);

    public void OnArbitration(string deviceId, int inputsCount, ArbitrationResult? result)
    {
        using var activity = ActivitySource.StartActivity("arbitration");
        activity?.SetTag("device_id", deviceId);
        activity?.SetTag("inputs_count", inputsCount);
        activity?.SetTag("winning_tier", result?.WinningTierId ?? "none");
        activity?.SetTag("winning_vdevice_id", result?.WinningVDeviceId.ToString() ?? "none");

        var record = new ArbitrationRecord(deviceId, inputsCount, result?.WinningTierId, DateTimeOffset.UtcNow);
        var queue = _recentActivity.GetOrAdd(deviceId, _ => new Queue<ArbitrationRecord>());
        lock (queue)
        {
            queue.Enqueue(record);
            while (queue.Count > MaxRecentPerDevice)
                queue.Dequeue();
        }
    }

    public IReadOnlyList<ArbitrationRecord> GetRecentActivity(string deviceId)
    {
        if (!_recentActivity.TryGetValue(deviceId, out var queue)) return [];
        lock (queue) return [.. queue];
    }
}

public sealed record ArbitrationRecord(
    string DeviceId,
    int InputsCount,
    string? WinningTierId,
    DateTimeOffset At);
