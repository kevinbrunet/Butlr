using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class CmdRunToolTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly CmdRunTool _tool;

    public CmdRunToolTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("alveus-cmdrun-").FullName;
        _tool = new CmdRunTool(_workspaceRoot);
    }

    public void Dispose()
    {
        _tool.Dispose();
        Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Fact]
    public async Task RunAsync_SimpleCommand_ReturnsOutputAndExitCode()
    {
        var result = await _tool.RunAsync("echo hello");

        Assert.Contains("hello", result);
        Assert.Contains("[exit code: 0]", result);
    }

    [Fact]
    public async Task RunAsync_FailingCommand_ReturnsNonZeroExitCode()
    {
        // `exit` directement tuerait le shell persistant : on passe par un sous-shell.
        var result = await _tool.RunAsync("(exit 7)");

        Assert.Contains("[exit code: 7]", result);
    }

    [Fact]
    public async Task RunAsync_ExportedVariable_PersistsAcrossCalls()
    {
        await _tool.RunAsync("export ALVEUS_TEST_VAR=42");

        var result = await _tool.RunAsync("echo $ALVEUS_TEST_VAR");

        Assert.Contains("42", result);
    }

    [Fact]
    public async Task RunAsync_StartsInWorkspaceRoot()
    {
        var result = await _tool.RunAsync("pwd");

        Assert.Contains(_workspaceRoot, result);
    }

    [Fact]
    public async Task RunAsync_RedirectsStderrToOutput()
    {
        var result = await _tool.RunAsync("echo err-message >&2");

        Assert.Contains("err-message", result);
        Assert.Contains("[exit code: 0]", result);
    }
}
