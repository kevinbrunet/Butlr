namespace Alveus.Web.Conversations;

/// <summary>
/// Stockage des conversations Alveus (cf. ADR 0027) — singleton en mémoire (pas de persistance,
/// cf. ADR 0027 §Conséquences). Sert à la fois de support à l'API HTTP
/// (<c>/v1/conversations/*</c>) et de point de rendez-vous entre le workflow Elsa (notifications,
/// activités) et les clients connectés (polling/SSE).
/// </summary>
public interface IConversationStore
{
    /// <summary>Crée une nouvelle conversation avec un identifiant généré, statut "running".</summary>
    ConversationState Create();

    ConversationState? Get(string conversationId);

    /// <summary>Ajoute un item et le diffuse aux abonnés SSE (<see cref="SubscribeAsync"/>).</summary>
    ConversationItem AddItem(
        string conversationId,
        string role,
        string text,
        ConversationItemKind kind,
        IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>Items dans l'ordre d'ajout, après l'item d'id <paramref name="after"/> (exclu) si fourni, limités à <paramref name="limit"/>.</summary>
    IReadOnlyList<ConversationItem> GetItems(string conversationId, string? after = null, int? limit = null);

    void SetWorkflowInstanceId(string conversationId, string workflowInstanceId);

    /// <summary>
    /// Enregistre le bookmark sur lequel le workflow est suspendu et passe
    /// <see cref="ConversationState.Status"/> à "awaiting_input".
    /// </summary>
    void SetPendingBookmark(string conversationId, string bookmarkId);

    /// <summary>
    /// Si la conversation est "awaiting_input", retourne (WorkflowInstanceId, BookmarkId)
    /// et repasse <see cref="ConversationState.Status"/> à "running" ; sinon <c>null</c>.
    /// </summary>
    (string WorkflowInstanceId, string BookmarkId)? TryResolvePendingBookmark(string conversationId);

    void Complete(string conversationId, bool failed = false);

    /// <summary>Flux des items postés après l'abonnement (pas d'historique — combiner avec <see cref="GetItems"/>).</summary>
    IAsyncEnumerable<ConversationItem> SubscribeAsync(string conversationId, CancellationToken cancellationToken);
}
