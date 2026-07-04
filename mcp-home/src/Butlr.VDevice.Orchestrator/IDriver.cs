using Butlr.VDevice.Core;

namespace Butlr.VDevice.Orchestrator;

public interface IDriver
{
    Task ApplyCommandAsync(string deviceId, ArbitrationResult? command, CancellationToken ct = default);
    IObservable<DeviceState> ObserveState();
}

public sealed record DeviceState(
    string DeviceId,
    object? RealState,
    string HealthStatus,   // "online" | "offline" | "degraded"
    DateTimeOffset UpdatedAt);
