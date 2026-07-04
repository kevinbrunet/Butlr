namespace Alveus.Web.Conversations;

public sealed record ConversationItemStreamEvent(ConversationItem Item)
    : WorkflowStreamEvent(Item.ConversationId);
