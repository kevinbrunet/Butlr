using System.Diagnostics;
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
    public async Task RunAsync_StartsInWorkerWorkspaceRoot()
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

    [Fact]
    public async Task RunAsync_WriteOutsideWorkspace_IsBlocked()
    {
        if (!CmdRunTool.IsBwrapAvailable)
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var markerPath = Path.Combine(home, $"alveus-cmdrun-escape-{Guid.NewGuid():N}");

        var result = await _tool.RunAsync($"echo escape > {markerPath}");

        Assert.Contains("Read-only file system", result);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task RunAsync_NohupBackgroundProcess_IsKilledOnDispose()
    {
        if (!CmdRunTool.IsBwrapAvailable)
        {
            return;
        }

        // `pgrep -f` voit le process même dans le namespace PID isolé du sandbox (même noyau,
        // juste une vue de PID différente). La durée sert de marqueur unique pour ce test.
        var sleepDuration = $"600.{Random.Shared.Next(100000, 999999)}";
        await _tool.RunAsync($"nohup sleep {sleepDuration} >/dev/null 2>&1 & disown");

        Assert.True(await ProcessExistsAsync(sleepDuration), "le process en arrière-plan devrait être démarré avant Dispose().");

        _tool.Dispose();

        Assert.True(await WaitUntilAsync(() => !ProcessExistsAsync(sleepDuration).Result, TimeSpan.FromSeconds(10)),
            "le process en arrière-plan devrait être tué quand Dispose() détruit le namespace PID (cf. ADR 0029).");
    }

    [Fact]
    public async Task RunAsync_ForegroundProcessExceedingTimeout_IsKilledOnDispose()
    {
        if (!CmdRunTool.IsBwrapAvailable)
        {
            return;
        }

        // Commande au premier plan qui dépasse le CommandTimeout (30s) de RunAsync : le shell
        // bash reste bloqué dans `wait()` sur ce process quand Dispose() est appelé.
        var sleepDuration = $"601.{Random.Shared.Next(100000, 999999)}";
        var runTask = _tool.RunAsync($"sleep {sleepDuration}");

        Assert.True(await WaitUntilAsync(() => ProcessExistsAsync(sleepDuration).Result, TimeSpan.FromSeconds(30)),
            "le process au premier plan devrait être démarré avant le timeout.");

        var result = await runTask;
        Assert.Contains("timeout", result);

        _tool.Dispose();

        Assert.True(await WaitUntilAsync(() => !ProcessExistsAsync(sleepDuration).Result, TimeSpan.FromSeconds(10)),
            "le process au premier plan (bash bloqué dans wait()) devrait être tué quand Dispose() détruit le namespace PID.");
    }

    private static async Task<bool> ProcessExistsAsync(string sleepDuration)
    {
        var startInfo = new ProcessStartInfo("pgrep")
        {
            ArgumentList = { "-f", $"sleep {sleepDuration}" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return !string.IsNullOrWhiteSpace(output);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return true;
    }
}
