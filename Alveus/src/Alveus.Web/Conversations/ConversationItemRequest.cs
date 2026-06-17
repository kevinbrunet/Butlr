namespace Alveus.Web.Conversations;

public sealed record ConversationItemRequest(string Role, List<ConversationContentPart> Content);
