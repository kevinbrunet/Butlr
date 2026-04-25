using Butlr.VDevice.Core;

namespace Butlr.VDevice.Orchestrator;

public abstract record VDeviceEvent(VDeviceId VDeviceId, string DeviceId, DateTimeOffset At);

public sealed record VDevicePreemptedEvent(VDeviceId VDeviceId, string DeviceId, string Reason, DateTimeOffset At)
    : VDeviceEvent(VDeviceId, DeviceId, At);

public sealed record VDeviceExpiredEvent(VDeviceId VDeviceId, string DeviceId, DateTimeOffset At)
    : VDeviceEvent(VDeviceId, DeviceId, At);
