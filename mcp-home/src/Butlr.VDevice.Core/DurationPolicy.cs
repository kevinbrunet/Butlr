namespace Butlr.VDevice.Core;

public sealed record DurationPolicy(
    bool PersistentAllowed,
    bool TtlRequired,
    int? TtlMaxMs);
