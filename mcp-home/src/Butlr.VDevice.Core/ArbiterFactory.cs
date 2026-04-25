using Butlr.VDevice.Core.Arbiters;

namespace Butlr.VDevice.Core;

public static class ArbiterFactory
{
    private static readonly Dictionary<string, IArbiter> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WinnerTakesAll"] = new WinnerTakesAllArbiter(),
        ["StrictPriority"] = new StrictPriorityArbiter(),
        ["UserPriorityThenTimestamp"] = new UserPriorityThenTimestampArbiter(),
        ["WeightedAverage"] = new WeightedAverageArbiter(),
    };

    private static readonly Dictionary<string, IArbiter> Custom = new(StringComparer.OrdinalIgnoreCase);

    public static IArbiter Resolve(string arbiterRef)
    {
        if (BuiltIn.TryGetValue(arbiterRef, out var builtin)) return builtin;
        if (Custom.TryGetValue(arbiterRef, out var custom)) return custom;
        throw new KeyNotFoundException($"Arbitre inconnu : '{arbiterRef}'");
    }

    public static IReadOnlyCollection<string> KnownRefs
        => [.. BuiltIn.Keys, .. Custom.Keys];

    // Enregistrement d'un arbitre custom (depuis assembly tierce, cf. ADR 0014)
    public static void Register(string arbiterRef, IArbiter arbiter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arbiterRef);
        ArgumentNullException.ThrowIfNull(arbiter);
        Custom[arbiterRef] = arbiter;
    }
}
