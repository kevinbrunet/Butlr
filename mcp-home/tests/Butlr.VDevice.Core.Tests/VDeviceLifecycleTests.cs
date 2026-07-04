using Butlr.VDevice.Core.Capabilities;

namespace Butlr.VDevice.Core.Tests;

public sealed class VDeviceLifecycleTests
{
    private static readonly TierRegistry Registry = TierRegistry.LoadDefault();
    private static readonly DateTimeOffset T0 = new(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);

    private static VDevice MakeTtl(int ttlMs) =>
        VDevice.Create("dev-1", "user_agent", "user-override", 80,
            ClusterId.OnOff, new AttributeId(0), true,
            new VDeviceDuration.Ttl(ttlMs), Registry, T0,
            actorUserId: "kevin", viaAgentId: "test");

    [Fact]
    public void IsExpired_TtlNotYetPassed_ReturnsFalse()
    {
        var vd = MakeTtl(60_000);
        Assert.False(VDeviceLifecycle.IsExpired(vd, T0.AddSeconds(30)));
    }

    [Fact]
    public void IsExpired_TtlPassed_ReturnsTrue()
    {
        var vd = MakeTtl(60_000);
        Assert.True(VDeviceLifecycle.IsExpired(vd, T0.AddSeconds(61)));
    }

    [Fact]
    public void Renew_WithinGrace_UpdatesLastRenewAt()
    {
        var vd = MakeTtl(60_000);
        var renewTime = T0.AddMilliseconds(VDeviceLifecycle.HeartbeatIntervalMs + VDeviceLifecycle.GraceMs - 1);
        var renewed = VDeviceLifecycle.Renew(vd, renewTime);
        Assert.Equal(renewTime, renewed.LastRenewAt);
    }

    [Fact]
    public void Renew_OneMillisecondAfterExpiry_SucceedsDueToGrace()
    {
        var vd = MakeTtl(60_000);
        // heartbeat + grace - 1ms = still in grace window
        var renewTime = T0.AddMilliseconds(VDeviceLifecycle.HeartbeatIntervalMs + VDeviceLifecycle.GraceMs - 1);
        var renewed = VDeviceLifecycle.Renew(vd, renewTime);
        Assert.NotNull(renewed);
    }

    [Fact]
    public void Renew_AfterGrace_Throws()
    {
        var vd = MakeTtl(60_000);
        var tooLate = T0.AddMilliseconds(VDeviceLifecycle.HeartbeatIntervalMs + VDeviceLifecycle.GraceMs + 1);
        Assert.Throws<InvalidOperationException>(() => VDeviceLifecycle.Renew(vd, tooLate));
    }

    [Fact]
    public void Tick_ExpiredVDevice_ReturnedInList()
    {
        var vd = MakeTtl(1_000);
        var expired = VDeviceLifecycle.Tick([vd], T0.AddSeconds(5));
        Assert.Contains(vd.Id, expired);
    }
}
