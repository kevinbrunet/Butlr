using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;

namespace Alveus.Web.Tools;

/// <summary>
/// Tool agent : consultation et édition de fichiers, restreint à <c>workspaceRoot</c>
/// (cf. ADR 0017). Toute opération sur un chemin résolu hors de ce répertoire est refusée.
/// Volontairement centré sur les fichiers — lister un répertoire relève de l'outil
/// d'exécution de commandes (<c>ls</c>), pour éviter le chevauchement entre les deux outils.
/// </summary>
public sealed class StrReplaceEditorTool
{
    private readonly string _workspaceRoot;

    // Historique d'édition par fichier, pour undo_edit. `null` = le fichier n'existait pas
    // avant l'edit (un undo doit donc le supprimer).
    private readonly ConcurrentDictionary<string, ConcurrentStack<string?>> _history = new();

    public StrReplaceEditorTool(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    [Description("Consulte ou édite des FICHIERS (pas des répertoires) dans le workspace de l'agent. Ce n'est PAS un shell : 'command' doit être l'une de view, create, str_replace, insert, undo_edit — jamais une commande shell comme 'ls' ou 'cat' (pour ça, ou pour lister un répertoire, utilise l'outil d'exécution de commandes). Commandes : 'view' (cat -n sur un fichier), 'create' (crée un fichier, échoue s'il existe déjà), 'str_replace' (remplace old_str par new_str, old_str doit être unique dans le fichier), 'insert' (insère new_str après la ligne insert_line), 'undo_edit' (annule le dernier edit sur path).")]
    public string Execute(
        [Description("Commande : view, create, str_replace, insert, undo_edit.")] string command,
        [Description("Chemin du fichier (pas d'un répertoire). Doit être relatif à la racine du workspace (ex. 'agent-edit.txt', pas '/workspace/agent-edit.txt') ; un chemin absolu hors du workspace est refusé.")] string path,
        [Description("Chaîne à rechercher (str_replace). Doit apparaître exactement une fois dans le fichier.")] string? old_str = null,
        [Description("Chaîne de remplacement (str_replace) ou contenu à insérer (insert).")] string? new_str = null,
        [Description("Contenu initial du fichier à créer (create).")] string? file_text = null,
        [Description("Numéro de ligne après laquelle insérer new_str (insert). 0 = début du fichier.")] int? insert_line = null)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var resolvedPath = ResolvePath(path);

            return command switch
            {
                "view" => View(resolvedPath),
                "create" => Create(resolvedPath, file_text),
                "str_replace" => StrReplace(resolvedPath, old_str, new_str),
                "insert" => Insert(resolvedPath, insert_line, new_str),
                "undo_edit" => UndoEdit(resolvedPath),
                _ => $"Erreur : commande inconnue '{command}'. Cet outil n'est pas un shell — attendu : view, create, str_replace, insert, undo_edit. Pour lister un répertoire, utilise 'view' avec ce path. Pour exécuter une commande shell, utilise l'outil d'exécution de commandes.",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Erreur : {ex.Message}";
        }
    }

    private string ResolvePath(string path)
    {
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(_workspaceRoot, path);
        var resolved = Path.GetFullPath(combined);

        var isInsideWorkspace = resolved == _workspaceRoot
            || resolved.StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (!isInsideWorkspace)
        {
            throw new InvalidOperationException($"Chemin hors du workspace autorisé : '{path}'.");
        }

        return resolved;
    }

    private static string View(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' est un répertoire — cet outil ne gère que des fichiers. Utilise l'outil "
                + "d'exécution de commandes (ex. 'ls') pour lister un répertoire.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier introuvable : '{path}'.");
        }

        var lines = File.ReadAllLines(path);
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').AppendLine(lines[i]);
        }

        return sb.ToString();
    }

    private string Create(string path, string? fileText)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Le fichier existe déjà : '{path}'. Utilise str_replace ou insert pour le modifier.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, fileText ?? string.Empty);
        PushHistory(path, previousContent: null);

        return $"Fichier créé : '{path}'.";
    }

    private string StrReplace(string path, string? oldStr, string? newStr)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier introuvable : '{path}'.");
        }

        ArgumentException.ThrowIfNullOrEmpty(oldStr);
        newStr ??= string.Empty;

        var content = File.ReadAllText(path);
        var occurrences = CountOccurrences(content, oldStr);

        if (occurrences == 0)
        {
            throw new InvalidOperationException($"old_str introuvable dans '{path}'.");
        }

        if (occurrences > 1)
        {
            throw new InvalidOperationException($"old_str apparaît {occurrences} fois dans '{path}' — il doit être unique.");
        }

        PushHistory(path, content);
        File.WriteAllText(path, content.Replace(oldStr, newStr, StringComparison.Ordinal));

        return $"Remplacement effectué dans '{path}'.";
    }

    private string Insert(string path, int? insertLine, string? newStr)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier introuvable : '{path}'.");
        }

        if (insertLine is null)
        {
            throw new ArgumentException("insert_line est requis pour la commande insert.", nameof(insertLine));
        }

        var lines = File.ReadAllLines(path).ToList();
        if (insertLine < 0 || insertLine > lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(insertLine), insertLine, $"insert_line doit être entre 0 et {lines.Count} (nombre de lignes de '{path}').");
        }

        PushHistory(path, File.ReadAllText(path));

        var newLines = (newStr ?? string.Empty).Split('\n');
        lines.InsertRange(insertLine.Value, newLines);
        File.WriteAllLines(path, lines);

        return $"Insertion effectuée dans '{path}' après la ligne {insertLine}.";
    }

    private string UndoEdit(string path)
    {
        if (!_history.TryGetValue(path, out var stack) || !stack.TryPop(out var previousContent))
        {
            throw new InvalidOperationException($"Aucun edit à annuler pour '{path}'.");
        }

        if (previousContent is null)
        {
            File.Delete(path);
            return $"Création annulée : '{path}' supprimé.";
        }

        File.WriteAllText(path, previousContent);
        return $"Dernier edit annulé pour '{path}'.";
    }

    private void PushHistory(string path, string? previousContent)
    {
        _history.GetOrAdd(path, static _ => new ConcurrentStack<string?>()).Push(previousContent);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
