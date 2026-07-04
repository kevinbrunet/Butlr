using Alveus.Web.Agents;

namespace Alveus.Web.Tests.Agent;

public sealed class CmdAgentWorkVerificationServiceTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("alveus-verification-").FullName;

    public void Dispose() => Directory.Delete(_workspaceRoot, recursive: true);

    [Fact]
    public async Task VerifyAsync_NoCommandConfigured_ReturnsPassed()
    {
        var service = new CmdAgentWorkVerificationService(_workspaceRoot, command: null);

        var result = await service.VerifyAsync();

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task VerifyAsync_CommandExitsZero_ReturnsSuccess()
    {
        var service = new CmdAgentWorkVerificationService(_workspaceRoot, command: "echo ok");

        var result = await service.VerifyAsync();

        Assert.True(result.Success);
        Assert.Contains("ok", result.Output);
    }

    [Fact]
    public async Task VerifyAsync_CommandExitsNonZero_ReturnsFailureWithOutput()
    {
        var service = new CmdAgentWorkVerificationService(_workspaceRoot, command: "echo erreur-test >&2; exit 1");

        var result = await service.VerifyAsync();

        Assert.False(result.Success);
        Assert.Contains("erreur-test", result.Output);
    }

    [Fact]
    public async Task VerifyAsync_RunsInWorkerWorkspaceRoot()
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, "marker.txt"), "x");
        var service = new CmdAgentWorkVerificationService(_workspaceRoot, command: "ls");

        var result = await service.VerifyAsync();

        Assert.True(result.Success);
        Assert.Contains("marker.txt", result.Output);
    }
}
