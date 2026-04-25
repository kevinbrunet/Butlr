using Butlr.VDevice.Core.Capabilities;

namespace Butlr.VDevice.Core.Tests;

public sealed class ArbitrationTests
{
    private static readonly TierRegistry Registry = TierRegistry.LoadDefault();
    private static readonly DateTimeOffset T0 = new(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);

    private static VDevice MakeApp(int priority, object value, DateTimeOffset? at = null) =>
        VDevice.Create("dev-1", "app", "apps", priority,
            ClusterId.OnOff, new AttributeId(0), value,
            new VDeviceDuration.Persistent(), Registry, at ?? T0, appId: "test");

    private static VDevice MakeUser(int priority, object value, DateTimeOffset? at = null) =>
        VDevice.Create("dev-1", "user_agent", "user-override", priority,
            ClusterId.OnOff, new AttributeId(0), value,
            new VDeviceDuration.Ttl(3_600_000), Registry, at ?? T0,
            actorUserId: "kevin", viaAgentId: "ui");

    [Fact]
    public void Resolve_NoVDevices_ReturnsNull()
    {
        var result = Arbitration.Resolve([], Registry);
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_AppOnly_ReturnsAppValue()
    {
        var app = MakeApp(50, true);
        var result = Arbitration.Resolve([app], Registry);
        Assert.NotNull(result);
        Assert.Equal(true, result.Value);
        Assert.Equal("apps", result.WinningTierId);
    }

    [Fact]
    public void Resolve_UserOverrideAndApp_UserWins()
    {
        var app = MakeApp(80, false);
        var user = MakeUser(50, true);

        var result = Arbitration.Resolve([app, user], Registry);
        Assert.NotNull(result);
        Assert.Equal("user-override", result.WinningTierId);
        Assert.Equal(true, result.Value);
    }

    [Fact]
    public void Resolve_TwoApps_HigherPriorityWins()
    {
        var low = MakeApp(20, false);
        var high = MakeApp(80, true);

        var result = Arbitration.Resolve([low, high], Registry);
        Assert.NotNull(result);
        Assert.Equal(true, result.Value);
    }

    [Fact]
    public void Resolve_TwoAppsEqualPriority_EarliestCreatedWins()
    {
        var first = MakeApp(50, "first", T0);
        var second = MakeApp(50, "second", T0.AddSeconds(1));

        var result = Arbitration.Resolve([first, second], Registry);
        Assert.NotNull(result);
        Assert.Equal("first", result.Value);
    }
}
