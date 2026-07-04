namespace Butlr.VDevice.Core.Events;

public sealed record VDeviceConflictResolved(
    string DeviceId,
    string TierId,
    VDeviceId WinningVDeviceId,
    IReadOnlyList<VDeviceId> LosingVDeviceIds,
    DateTimeOffset ResolvedAt);
