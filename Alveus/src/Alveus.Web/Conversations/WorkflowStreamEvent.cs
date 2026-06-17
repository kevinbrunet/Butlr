using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

/// <summary>
/// Union discriminée des événements diffusés dans le flux d'observabilité d'une conversation.
/// Combine les items haute-niveau (<see cref="ConversationItemStreamEvent"/>), les échanges LLM
/// bruts (<see cref="LlmExchangeStreamEvent"/>), les signaux de suspension
/// (<see cref="WorkflowSuspendedStreamEvent"/>) et de complétion
/// (<see cref="WorkflowCompletedStreamEvent"/>).
/// </summary>
public abstract record WorkflowStreamEvent(string ConversationId);

public sealed record ConversationItemStreamEvent(ConversationItem Item)
    : WorkflowStreamEvent(Item.ConversationId);

public sealed record LlmExchangeStreamEvent(
    string ConversationId,
    string AgentName,
    ChatResponse Response)
    : WorkflowStreamEvent(ConversationId);

/// <summary>
/// Le workflow est suspendu en attente d'une réponse humaine
/// (<see cref="Activities.AwaitConversationReply"/> — bookmark Elsa actif). Le client doit
/// envoyer la réponse sur le même canal (WebSocket) ou via <c>POST /v1/conversations/{id}/items</c>.
/// </summary>
public sealed record WorkflowSuspendedStreamEvent(string ConversationId)
    : WorkflowStreamEvent(ConversationId);

public sealed record WorkflowCompletedStreamEvent(string ConversationId, string Status)
    : WorkflowStreamEvent(ConversationId);
