using System.ComponentModel;
using Alveus.Web.Conversations;

namespace Alveus.Web.Tools;

/// <summary>
/// Wrapper de <see cref="StrReplaceEditorTool"/> (cf. ADR 0027) : délègue chaque appel à
/// <paramref name="inner"/> sans le modifier (préserve ses tests existants), puis — si une
/// conversation est active (<see cref="IConversationContextAccessor.ConversationId"/>, posée par
/// l'activité agent en cours, cf. <see cref="Activities.AgentPromptActivityBase"/> et
/// <see cref="Activities.MeetingActivityBase"/>) et que <c>command != "view"</c> — poste un item
/// <see cref="ConversationItemKind.FileEdit"/> dans <see cref="IConversationStore"/>. La signature et
/// les <see cref="DescriptionAttribute"/> de <see cref="texteditor"/> sont identiques à celles de
/// <see cref="StrReplaceEditorTool.texteditor"/> : c'est ce contrat que voit le LLM via
/// <c>AIFunctionFactory.Create</c>.
/// </summary>
public sealed class ConversationAwareStrReplaceEditorTool
{
    private readonly StrReplaceEditorTool _inner;
    private readonly IConversationContextAccessor _contextAccessor;
    private readonly IConversationStore _store;
    private readonly string _agentDisplayName;

    public ConversationAwareStrReplaceEditorTool(
        StrReplaceEditorTool inner,
        IConversationContextAccessor contextAccessor,
        IConversationStore store,
        string agentDisplayName)
    {
        _inner = inner;
        _contextAccessor = contextAccessor;
        _store = store;
        _agentDisplayName = agentDisplayName;
    }

    [Description("Consulte ou édite des FICHIERS (pas des répertoires) dans le workspace de l'agent. Ce n'est PAS un shell : 'command' doit être l'une de view, create, str_replace, insert, undo_edit — jamais une commande shell comme 'ls' ou 'cat' (pour ça, ou pour lister un répertoire, utilise l'outil d'exécution de commandes). Commandes : 'view' (cat -n sur un fichier), 'create' (crée un fichier, échoue s'il existe déjà), 'str_replace' (remplace old_str par new_str, old_str doit être unique dans le fichier), 'insert' (insère new_str après la ligne insert_line), 'undo_edit' (annule le dernier edit sur path).")]
    public string texteditor(
        [Description("Commande : view, create, str_replace, insert, undo_edit.")] string command,
        [Description("Chemin du fichier (pas d'un répertoire). Doit être relatif à la racine du workspace (ex. 'agent-edit.txt', pas '/workspace/agent-edit.txt') ; un chemin absolu hors du workspace est refusé.")] string path,
        [Description("Chaîne à rechercher (str_replace). Doit apparaître exactement une fois dans le fichier.")] string? old_str = null,
        [Description("Chaîne de remplacement (str_replace) ou contenu à insérer (insert).")] string? new_str = null,
        [Description("Contenu initial du fichier à créer (create).")] string? file_text = null,
        [Description("Numéro de ligne après laquelle insérer new_str (insert). 0 = début du fichier.")] int? insert_line = null)
    {
        var result = _inner.texteditor(command, path, old_str, new_str, file_text, insert_line);

        var conversationId = _contextAccessor.ConversationId;
        if (command != "view" && !string.IsNullOrEmpty(conversationId))
        {
            _store.AddItem(
                conversationId,
                "assistant",
                result,
                ConversationItemKind.FileEdit,
                new Dictionary<string, string>
                {
                    ["agent"] = _agentDisplayName,
                    ["command"] = command,
                    ["path"] = path,
                });
        }

        return result;
    }
}
