using Butlr.VDevice.Core.Arbiters;
using Butlr.VDevice.Core.Capabilities;

namespace Butlr.VDevice.Core.Tests;

public sealed class ArbiterTests
{
    private static readonly TierRegistry Registry = TierRegistry.LoadDefault();
    private static readonly DateTimeOffset T0 = new(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);

    private static VDevice MakeApp(int priority, object value, DateTimeOffset? at = null) =>
        VDevice.Create("dev", "app", "apps", priority,
            ClusterId.OnOff, new AttributeId(0), value,
            new VDeviceDuration.Persistent(), Registry, at ?? T0, appId: "test");

    // WinnerTakesAll

    [Fact]
    public void WinnerTakesAll_Empty_ReturnsNull()
    {
        var arbiter = new WinnerTakesAllArbiter();
        Assert.Null(arbiter.Arbitrate([], null));
    }

    [Fact]
    public void WinnerTakesAll_Single_ReturnsThatValue()
    {
        var arbiter = new WinnerTakesAllArbiter();
        var vd = MakeApp(50, "winner");
        Assert.Equal("winner", arbiter.Arbitrate([vd], null));
    }

    // StrictPriority

    [Fact]
    public void StrictPriority_HigherPriorityWins()
    {
        var arbiter = new StrictPriorityArbiter();
        var low = MakeApp(10, "low");
        var high = MakeApp(90, "high");
        Assert.Equal("high", arbiter.Arbitrate([low, high], null));
    }

    [Fact]
    public void StrictPriority_EqualPriority_EarliestCreatedWins()
    {
        var arbiter = new StrictPriorityArbiter();
        var first = MakeApp(50, "first", T0);
        var second = MakeApp(50, "second", T0.AddSeconds(1));
        Assert.Equal("first", arbiter.Arbitrate([first, second], null));
    }

    // WeightedAverage

    [Fact]
    public void WeightedAverage_TwoNumeric_ReturnsWeightedMean()
    {
        var arbiter = new WeightedAverageArbiter();
        var a = MakeApp(100, 20.0);
        var b = MakeApp(100, 30.0);
        var result = (double?)arbiter.Arbitrate([a, b], null);
        Assert.NotNull(result);
        Assert.Equal(25.0, result!.Value, precision: 5);
    }

    [Fact]
    public void WeightedAverage_NonNumeric_Throws()
    {
        var arbiter = new WeightedAverageArbiter();
        var vd = MakeApp(50, "non-numeric");
        Assert.Throws<InvalidOperationException>(() => arbiter.Arbitrate([vd], null));
    }
}
