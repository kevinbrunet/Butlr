using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Alveus.Web.Tools;

/// <summary>
/// Tool agent : exécute des commandes dans un shell bash persistant et renvoie leur sortie.
/// Le shell tourne dans une sandbox <c>bwrap</c> confinée à <c>workspaceRoot</c> (cf. ADR 0029,
/// qui remplace le scoping non garanti d'ADR 0017) : système de fichiers en lecture seule sauf
/// <c>workspaceRoot</c>, <c>~/.nuget</c>, <c>~/.dotnet</c> et un <c>/tmp</c> isolé, et namespace
/// PID dédié pour que <see cref="Dispose"/> tue aussi les process détachés (<c>nohup ... &amp;
/// disown</c>). Si <c>bwrap</c> est indisponible, fallback sur un <c>bash</c> direct (comme avant
/// ADR 0029) avec un avertissement loggé.
/// </summary>
public sealed class CmdRunTool : IDisposable
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<bool> BwrapAvailableLazy = new(() => FindOnPath("bwrap") is not null);

    private readonly Process _shell;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string? _bwrapKillSignature;
    private readonly string _workspaceRoot;
    private readonly TimeSpan _commandTimeout;
    private bool _disposed;

    public static bool IsBwrapAvailable => BwrapAvailableLazy.Value;

    public CmdRunTool(string workspaceRoot, ILogger<CmdRunTool>? logger = null, TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;

        Directory.CreateDirectory(workspaceRoot);
        _workspaceRoot = workspaceRoot;

        _shell = new Process
        {
            StartInfo = BuildStartInfo(workspaceRoot, logger),
        };
        _shell.Start();

        if (IsBwrapAvailable)
        {
            // Signature unique de cette sandbox (le bind read-write de workspaceRoot n'apparaît
            // que dans les arguments de CE process bwrap). Sert de filet de sécurité dans
            // Dispose() : cf. commentaire associé.
            _bwrapKillSignature = $"--bind {workspaceRoot} {workspaceRoot}";
        }

        // Fusionne stderr dans stdout pour tout le reste de la session : on n'a qu'un
        // seul flux à lire pour détecter la fin d'une commande.
        _shell.StandardInput.WriteLine("exec 2>&1");

        // ~/.dotnet n'est pas dans PATH par défaut (installation locale). On l'ajoute pour que
        // `dotnet` soit utilisable directement dans le shell de l'agent.
        var dotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        if (Directory.Exists(dotnetDir))
        {
            _shell.StandardInput.WriteLine($"export PATH=\"{dotnetDir}:$PATH\"");
            _shell.StandardInput.WriteLine($"export DOTNET_ROOT=\"{dotnetDir}\"");
        }
    }

    private static ProcessStartInfo BuildStartInfo(string workspaceRoot, ILogger<CmdRunTool>? logger)
    {
        if (!IsBwrapAvailable)
        {
            logger?.LogWarning("bwrap introuvable dans PATH : CmdRunTool utilise un bash non sandboxé (cf. ADR 0029).");

            return new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workspaceRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "bwrap",
            WorkingDirectory = workspaceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in new[]
        {
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // ~/.cache/ms-playwright : le CLI playwright install a besoin d'accès en écriture pour
        // vérifier/extraire les navigateurs même quand ils sont déjà présents.
        foreach (var rwPath in new[] {
            Path.Combine(home, ".nuget"),
            Path.Combine(home, ".dotnet"),
            Path.Combine(home, ".cache", "ms-playwright"),
            workspaceRoot })
        {
            if (Directory.Exists(rwPath))
            {
                startInfo.ArgumentList.Add("--bind");
                startInfo.ArgumentList.Add(rwPath);
                startInfo.ArgumentList.Add(rwPath);
            }
        }

        startInfo.ArgumentList.Add("--chdir");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add("--unshare-pid");
        startInfo.ArgumentList.Add("/bin/bash");

        return startInfo;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Description("Exécute une commande dans un shell bash persistant (état conservé entre les appels : cwd, variables, etc.) et renvoie sa sortie (stdout+stderr) et son code de retour. Pour consulter ou modifier un fichier précis (lire, créer, remplacer du texte), préfère l'outil d'édition de fichiers — plus fiable que cat/sed/echo.")]
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
            timeoutCts.CancelAfter(_commandTimeout);

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
            return $"[timeout après {_commandTimeout.TotalSeconds}s — la commande continue peut-être en arrière-plan et perturber l'appel suivant]";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Erreur shell : {ex.Message}";
        }
        finally
        {
            _lock.Release();
        }
    }

    // Remet le cwd du shell persistant sur workspaceRoot. À appeler entre les tests qui partagent
    // le même outil singleton, au cas où un test précédent aurait fait `cd /tmp` ou similaire.
    public Task ResetWorkingDirectoryAsync(CancellationToken cancellationToken = default)
        => RunAsync($"cd '{_workspaceRoot}'", cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_shell.HasExited)
            {
                _shell.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Process déjà sorti entre le check et le Kill, ou un de ses descendants a
            // disparu pendant le parcours de l'arbre (race) : le filet de sécurité
            // ci-dessous (pkill par signature) prend le relais.
        }

        // Filet de sécurité : bwrap lance un process intermédiaire qui devient le PID 1 du
        // nouveau namespace PID (cf. ADR 0029) — ce n'est pas toujours celui que _shell
        // référence. Kill(entireProcessTree: true) sur _shell peut alors ne tuer qu'une
        // partie de l'arbre et laisser le PID 1 du namespace (et ses enfants, ex. un
        // `dotnet run` détaché) orphelins. On force la mort de tout process bwrap portant
        // la signature unique de ce workspace : tuer le PID 1 d'un namespace PID fait que
        // le noyau tue tout le reste du namespace (pid_namespaces(7)).
        if (_bwrapKillSignature is not null)
        {
            try
            {
                // `--` avant le pattern : sans ça, getopt interprète le `--bind` du pattern
                // comme une option de pkill et échoue (exit 2, affiche l'aide).
                using var pkill = Process.Start(new ProcessStartInfo("pkill")
                {
                    ArgumentList = { "-9", "-f", "--", _bwrapKillSignature },
                    UseShellExecute = false,
                });
                pkill?.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // pkill indisponible ou échec : pas critique, _shell.Kill ci-dessus reste
                // la voie principale.
            }
        }

        _shell.Dispose();
        _lock.Dispose();
    }
}
