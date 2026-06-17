namespace Alveus.Web.Conversations;

/// <summary>
/// Le workflow est suspendu en attente d'une réponse humaine
/// (<see cref="Activities.AwaitConversationReply"/> — bookmark Elsa actif). Le client doit
/// envoyer la réponse sur le même canal (WebSocket) ou via <c>POST /v1/conversations/{id}/items</c>.
/// </summary>
public sealed record WorkflowSuspendedStreamEvent(string ConversationId)
    : WorkflowStreamEvent(ConversationId);
