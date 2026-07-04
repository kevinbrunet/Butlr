namespace Butlr.VDevice.Core;

public sealed record ArbitrationResult(
    object Value,
    string WinningTierId,
    VDeviceId WinningVDeviceId,
    bool BypassInertia);
