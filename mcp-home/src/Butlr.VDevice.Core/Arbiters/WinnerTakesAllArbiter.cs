namespace Butlr.VDevice.Core.Arbiters;

// Premier VDevice admis gagne (utile pour niveau safety — mono-émetteur attendu)
public sealed class WinnerTakesAllArbiter : IArbiter
{
    public object? Arbitrate(IReadOnlyCollection<VDevice> admitted, object? realState)
        => admitted.Count == 0 ? null : admitted.First().Value;

    public VDeviceId? WinningId(IReadOnlyCollection<VDevice> admitted, object? realState)
        => admitted.Count == 0 ? null : admitted.First().Id;
}
