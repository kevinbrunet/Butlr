namespace Butlr.VDevice.Core;

public sealed class TierRegistry
{
    private readonly IReadOnlyDictionary<string, Tier> _byId;
    private readonly IReadOnlyList<Tier> _ordered;

    private TierRegistry(IReadOnlyList<Tier> ordered)
    {
        _ordered = ordered;
        _byId = ordered.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static TierRegistry Load(IEnumerable<Tier> tiers, IEnumerable<string> knownArbiterRefs)
    {
        var list = tiers.ToList();

        var duplicateRank = list.GroupBy(t => t.Rank).FirstOrDefault(g => g.Count() > 1);
        if (duplicateRank is not null)
            throw new ArgumentException($"Rank dupliqué : {duplicateRank.Key}");

        var duplicateId = list.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
            throw new ArgumentException($"Id dupliqué : {duplicateId.Key}");

        var known = new HashSet<string>(knownArbiterRefs, StringComparer.OrdinalIgnoreCase);
        foreach (var tier in list)
        {
            if (!known.Contains(tier.ArbiterRef))
                throw new ArgumentException($"ArbiterRef inconnu '{tier.ArbiterRef}' sur le niveau '{tier.Id}'");
        }

        var ordered = list.OrderBy(t => t.Rank).ToList();
        return new TierRegistry(ordered);
    }

    public static TierRegistry LoadDefault()
    {
        var knownRefs = new[] { "WinnerTakesAll", "StrictPriority", "UserPriorityThenTimestamp", "WeightedAverage" };
        return Load(Tier.DefaultPreset, knownRefs);
    }

    public Tier? TryGet(string tierId) => _byId.TryGetValue(tierId, out var t) ? t : null;

    public Tier Get(string tierId) =>
        _byId.TryGetValue(tierId, out var t) ? t : throw new KeyNotFoundException($"Niveau inconnu : {tierId}");

    public IReadOnlyList<Tier> Ordered => _ordered;

    public bool Contains(string tierId) => _byId.ContainsKey(tierId);
}
