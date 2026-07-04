using System.Reactive.Subjects;
using Butlr.VDevice.Core;

namespace Butlr.VDevice.Orchestrator;

// Driver en mémoire pour les tests — log les commandes et expose un état contrôlable
public sealed class InMemoryDriver : IDriver, IDisposable
{
    private readonly Subject<DeviceState> _states = new();
    private readonly List<(string DeviceId, ArbitrationResult? Command, DateTimeOffset At)> _log = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<(string DeviceId, ArbitrationResult? Command, DateTimeOffset At)> CommandLog
    {
        get { lock (_lock) return [.. _log]; }
    }

    public Task ApplyCommandAsync(string deviceId, ArbitrationResult? command, CancellationToken ct = default)
    {
        lock (_lock)
            _log.Add((deviceId, command, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public IObservable<DeviceState> ObserveState() => _states;

    public void PushState(DeviceState state) => _states.OnNext(state);

    public void Dispose() => _states.Dispose();
}
