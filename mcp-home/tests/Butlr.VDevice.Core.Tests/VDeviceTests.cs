using Butlr.VDevice.Core.Capabilities;

namespace Butlr.VDevice.Core.Tests;

public sealed class VDeviceTests
{
    private static readonly TierRegistry Registry = TierRegistry.LoadDefault();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Create_ValidApp_Succeeds()
    {
        var vd = VDevice.Create(
            deviceId: "lumiere-salon",
            actorKind: "app",
            tierId: "apps",
            priority: 50,
            cluster: ClusterId.OnOff,
            attribute: new AttributeId(0x0000),
            value: true,
            duration: new VDeviceDuration.Persistent(),
            registry: Registry,
            now: Now,
            appId: "cocooning");

        Assert.Equal("lumiere-salon", vd.DeviceId);
        Assert.Equal("apps", vd.TierId);
    }

    [Fact]
    public void Create_AppOnUserOverride_ThrowsTagMismatch()
    {
        Assert.Throws<ArgumentException>(() => VDevice.Create(
            deviceId: "lumiere-salon",
            actorKind: "app",
            tierId: "user-override",
            priority: 50,
            cluster: ClusterId.OnOff,
            attribute: new AttributeId(0x0000),
            value: true,
            duration: new VDeviceDuration.Ttl(60_000),
            registry: Registry,
            now: Now));
    }

    [Fact]
    public void Create_UserOverrideWithoutTtl_Throws()
    {
        Assert.Throws<ArgumentException>(() => VDevice.Create(
            deviceId: "lumiere-salon",
            actorKind: "user_agent",
            tierId: "user-override",
            priority: 80,
            cluster: ClusterId.OnOff,
            attribute: new AttributeId(0x0000),
            value: true,
            duration: new VDeviceDuration.Persistent(),
            registry: Registry,
            now: Now,
            actorUserId: "kevin",
            viaAgentId: "carson"));
    }

    [Fact]
    public void Create_UserOverrideWithTtl_Succeeds()
    {
        var vd = VDevice.Create(
            deviceId: "lumiere-salon",
            actorKind: "user_agent",
            tierId: "user-override",
            priority: 80,
            cluster: ClusterId.OnOff,
            attribute: new AttributeId(0x0000),
            value: true,
            duration: new VDeviceDuration.Ttl(1_800_000),
            registry: Registry,
            now: Now,
            actorUserId: "kevin",
            viaAgentId: "carson");

        Assert.Equal("user-override", vd.TierId);
        Assert.Equal("kevin", vd.ActorUserId);
    }

    [Fact]
    public void ResolveTierId_App_ResolvesToApps()
    {
        var tierId = VDevice.ResolveTierId("app", Registry);
        Assert.Equal("apps", tierId);
    }

    [Fact]
    public void ResolveTierId_UserAgent_ResolvesToUserOverride()
    {
        var tierId = VDevice.ResolveTierId("user_agent", Registry);
        Assert.Equal("user-override", tierId);
    }

    [Fact]
    public void Create_PriorityOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VDevice.Create(
            deviceId: "lumiere-salon",
            actorKind: "app",
            tierId: "apps",
            priority: 150,
            cluster: ClusterId.OnOff,
            attribute: new AttributeId(0x0000),
            value: true,
            duration: new VDeviceDuration.Persistent(),
            registry: Registry,
            now: Now));
    }
}
