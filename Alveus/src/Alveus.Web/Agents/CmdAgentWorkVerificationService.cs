using System.Diagnostics;
using System.Text;

namespace Alveus.Web.Agents;

/// <summary>
/// Implémentation par défaut de <see cref="IAgentWorkVerificationService"/> (cf. ADR 0020) :
/// exécute une commande shell configurée (<c>Agent:VerificationCommand</c>) dans le workspace de
/// l'agent et considère le travail validé si son code de sortie est 0. Si la commande n'est pas
/// configurée, la vérification est un no-op qui valide toujours — utile pour les déploiements/tests
/// qui n'ont pas (encore) de script de validation.
/// </summary>
public sealed class CmdAgentWorkVerificationService : IAgentWorkVerificationService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly string _workspaceRoot;
    private readonly string? _command;

    public CmdAgentWorkVerificationService(string workspaceRoot, string? command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = workspaceRoot;
        _command = string.IsNullOrWhiteSpace(command) ? null : command;
    }

    public async ValueTask<AgentWorkVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (_command is null)
        {
            return AgentWorkVerificationResult.Passed;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-c", _command },
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new AgentWorkVerificationResult(false, $"[timeout après {CommandTimeout.TotalSeconds}s de la commande de vérification]");
        }

        return new AgentWorkVerificationResult(process.ExitCode == 0, output.ToString());
    }
}
