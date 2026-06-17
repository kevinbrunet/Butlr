using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

public sealed record LlmExchangeStreamEvent(
    string ConversationId,
    string AgentName,
    ChatResponse Response)
    : WorkflowStreamEvent(ConversationId);
