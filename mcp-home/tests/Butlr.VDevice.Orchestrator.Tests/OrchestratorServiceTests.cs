using Butlr.VDevice.Core;
using Butlr.VDevice.Core.Capabilities;
using Butlr.VDevice.Core.Capabilities.Clusters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Butlr.VDevice.Orchestrator.Tests;

public sealed class OrchestratorServiceTests : IDisposable
{
    private readonly TierRegistry _registry = TierRegistry.LoadDefault();
    private readonly InMemoryDriver _driver = new();
    private readonly OrchestratorService _svc;

    public OrchestratorServiceTests()
    {
        var obs = new ObservabilityService(NullLogger<ObservabilityService>.Instance);
        _svc = new OrchestratorService(_registry, _driver, obs, NullLogger<OrchestratorService>.Instance);
    }

    public void Dispose() => _driver.Dispose();

    [Fact]
    public async Task Create_ThenRelease_DriverReceivesNullCommand()
    {
        var vd = await _svc.CreateVDeviceAsync(
            "lumiere-salon", "app", "apps", 50,
            ClusterId.OnOff, new AttributeId(0), true,
            new VDeviceDuration.Persistent(), appId: "test");

        Assert.Single(_driver.CommandLog);
        Assert.Equal(true, _driver.CommandLog[0].Command?.Value);

        await _svc.ReleaseVDeviceAsync(vd.Id);
        Assert.Equal(2, _driver.CommandLog.Count);
        Assert.Null(_driver.CommandLog[1].Command);
    }

    [Fact]
    public async Task Create_HigherPriorityWins()
    {
        await _svc.CreateVDeviceAsync(
            "lumiere-salon", "app", "apps", 30,
            ClusterId.OnOff, new AttributeId(0), false,
            new VDeviceDuration.Persistent(), appId: "low");

        await _svc.CreateVDeviceAsync(
            "lumiere-salon", "app", "apps", 80,
            ClusterId.OnOff, new AttributeId(0), true,
            new VDeviceDuration.Persistent(), appId: "high");

        var last = _driver.CommandLog.Last();
        Assert.Equal(true, last.Command?.Value);
    }

    [Fact]
    public async Task UserOverride_WinsOverApp()
    {
        await _svc.CreateVDeviceAsync(
            "thermostat-salon", "app", "apps", 80,
            ClusterId.Thermostat, ThermostatCluster.Attributes.OccupiedHeatingSetpoint, 2000,
            new VDeviceDuration.Persistent(), appId: "cocooning");

        await _svc.CreateVDeviceAsync(
            "thermostat-salon", "user_agent", "user-override", 100,
            ClusterId.Thermostat, ThermostatCluster.Attributes.OccupiedHeatingSetpoint, 2300,
            new VDeviceDuration.Ttl(3_600_000),
            actorUserId: "kevin", viaAgentId: "carlson");

        var last = _driver.CommandLog.Last();
        Assert.Equal(2300, last.Command?.Value);
        Assert.Equal("user-override", last.Command?.WinningTierId);
    }
}
