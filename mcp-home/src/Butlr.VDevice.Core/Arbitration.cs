namespace Butlr.VDevice.Core;

// Service pur sans état — fonction d'arbitrage sur un snapshot de VDevices
public static class Arbitration
{
    public static ArbitrationResult? Resolve(
        IReadOnlyCollection<VDevice> activeVDevices,
        TierRegistry registry,
        object? realState = null)
    {
        // Strict winner-takes-all entre niveaux (rank croissant = priorité décroissante)
        foreach (var tier in registry.Ordered)
        {
            var admitted = activeVDevices
                .Where(v => string.Equals(v.TierId, tier.Id, StringComparison.OrdinalIgnoreCase)
                            && tier.Admission.Matches(v.Tags))
                .ToList();

            if (admitted.Count == 0) continue;

            var arbiter = ArbiterFactory.Resolve(tier.ArbiterRef);
            var value = arbiter.Arbitrate(admitted, realState);
            if (value is null) continue;

            var winnerId = arbiter.WinningId(admitted, realState);
            if (winnerId is null) continue;

            return new ArbitrationResult(value, tier.Id, winnerId.Value, tier.BypassInertia);
        }

        return null;
    }
}
