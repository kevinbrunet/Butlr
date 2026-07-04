namespace Butlr.VDevice.Core.Tests;

public sealed class TierRegistryTests
{
    [Fact]
    public void LoadDefault_ValidPreset_Succeeds()
    {
        var registry = TierRegistry.LoadDefault();
        Assert.Equal(3, registry.Ordered.Count);
    }

    [Fact]
    public void Load_DuplicateRank_Throws()
    {
        var tiers = new[]
        {
            new Tier("a", 1, "WinnerTakesAll", new Dictionary<string, string>(), Admission.Any,
                new DurationPolicy(true, false, null), false),
            new Tier("b", 1, "WinnerTakesAll", new Dictionary<string, string>(), Admission.Any,
                new DurationPolicy(true, false, null), false),
        };
        Assert.Throws<ArgumentException>(() => TierRegistry.Load(tiers, ["WinnerTakesAll"]));
    }

    [Fact]
    public void Load_UnknownArbiterRef_Throws()
    {
        var tiers = new[]
        {
            new Tier("a", 1, "InconnnuArbiter", new Dictionary<string, string>(), Admission.Any,
                new DurationPolicy(true, false, null), false),
        };
        Assert.Throws<ArgumentException>(() => TierRegistry.Load(tiers, ["WinnerTakesAll"]));
    }

    [Fact]
    public void Ordered_SortedByRankAscending()
    {
        var registry = TierRegistry.LoadDefault();
        var ranks = registry.Ordered.Select(t => t.Rank).ToList();
        Assert.Equal(ranks.OrderBy(r => r).ToList(), ranks);
    }
}
