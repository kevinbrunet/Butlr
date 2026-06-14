namespace Alveus.Web.Conversations;

/// <summary>
/// Identifiant de conversation ambiant pour le thread d'exécution courant (cf. ADR 0027) — permet à
/// un outil agent (ex. <c>ConversationAwareStrReplaceEditorTool</c>) de poster des items sans que
/// cet identifiant transite par la signature exposée au LLM.
/// </summary>
public interface IConversationContextAccessor
{
    string? ConversationId { get; set; }
}
