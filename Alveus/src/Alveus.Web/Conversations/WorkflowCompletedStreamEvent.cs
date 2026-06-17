namespace Alveus.Web.Conversations;

public sealed record WorkflowCompletedStreamEvent(string ConversationId, string Status)
    : WorkflowStreamEvent(ConversationId);
