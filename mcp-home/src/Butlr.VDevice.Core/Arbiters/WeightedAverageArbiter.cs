namespace Butlr.VDevice.Core.Arbiters;

// Moyenne pondérée des valeurs — uniquement pour attributs numériques continus
public sealed class WeightedAverageArbiter : IArbiter
{
    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        _ => throw new InvalidOperationException(
            $"WeightedAverageArbiter ne supporte que les types numériques, reçu : {value.GetType().Name}")
    };

    public object? Arbitrate(IReadOnlyCollection<VDevice> admitted, object? realState)
    {
        if (admitted.Count == 0) return null;

        double totalWeight = admitted.Sum(v => v.Priority);
        if (totalWeight == 0) return null;

        double weighted = admitted.Sum(v => ToDouble(v.Value) * v.Priority) / totalWeight;
        return weighted;
    }

    public VDeviceId? WinningId(IReadOnlyCollection<VDevice> admitted, object? realState)
    {
        // Aucun "winner" unique — renvoie le plus prioritaire pour la traçabilité
        if (admitted.Count == 0) return null;
        return admitted.OrderByDescending(v => v.Priority).First().Id;
    }
}
