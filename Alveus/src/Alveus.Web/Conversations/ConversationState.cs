using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Alveus.Web.Conversations;

/// <summary>
/// État mutable d'une conversation (cf. ADR 0027). <see cref="Items"/> est append-only et protégé
/// par <see cref="Lock"/> ; <see cref="Subscribers"/> reçoit chaque nouvel item pour le streaming SSE
/// (<c>GET /v1/conversations/{id}/stream</c>).
/// </summary>
public sealed class ConversationState
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary><c>running</c>, <c>awaiting_input</c>, <c>completed</c> ou <c>failed</c>.</summary>
    public string Status { get; set; } = "running";

    /// <summary>Identifiant de l'instance <see cref="AlveusTaskWorkflow"/> démarrée pour cette conversation.</summary>
    public string? WorkflowInstanceId { get; set; }

    /// <summary>
    /// Bookmark en attente de réponse humaine (<see cref="Activities.AwaitConversationReply"/>),
    /// posé quand <see cref="Status"/> vaut <c>awaiting_input</c>.
    /// </summary>
    public string? PendingBookmarkId { get; set; }

    public readonly object Lock = new();

    public List<ConversationItem> Items { get; } = [];

    public ConcurrentDictionary<Guid, Channel<ConversationItem>> Subscribers { get; } = new();
}
