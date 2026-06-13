using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Alveus.Web.Tools;

/// <summary>
/// Tool agent : exécute des commandes dans un shell bash persistant et renvoie leur sortie.
/// Le shell démarre dans <c>workspaceRoot</c> (cf. ADR 0017) — ce répertoire de départ est
/// une commodité, pas une sandbox : une commande peut en sortir (chemins absolus, <c>cd</c>).
/// </summary>
public sealed class CmdRunTool : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _shell;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CmdRunTool(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _shell = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workspaceRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _shell.Start();

        // Fusionne stderr dans stdout pour tout le reste de la session : on n'a qu'un
        // seul flux à lire pour détecter la fin d'une commande.
        _shell.StandardInput.WriteLine("exec 2>&1");
    }

    [Description("Exécute une commande dans un shell bash persistant (état conservé entre les appels : cwd, variables, etc.) et renvoie sa sortie (stdout+stderr) et son code de retour.")]
    public async Task<string> RunAsync(
        [Description("Commande shell à exécuter.")] string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var marker = $"__CMDRUN_{Guid.NewGuid():N}__";
            await _shell.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await _shell.StandardInput.WriteLineAsync($"echo {marker}$?").ConfigureAwait(false);
            await _shell.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandTimeout);

            var output = new StringBuilder();
            string? line;
            while ((line = await _shell.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) != null)
            {
                if (line.StartsWith(marker, StringComparison.Ordinal))
                {
                    var exitCode = line[marker.Length..];
                    output.Append($"[exit code: {exitCode}]");
                    return output.ToString();
                }

                output.AppendLine(line);
            }

            return output.Append("[shell terminé de manière inattendue]").ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"[timeout après {CommandTimeout.TotalSeconds}s — la commande continue peut-être en arrière-plan et perturber l'appel suivant]";
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_shell.HasExited)
            {
                _shell.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process déjà sorti entre le check et le Kill : rien à faire.
        }

        _shell.Dispose();
        _lock.Dispose();
    }
}
