namespace Alveus.Web.Conversations;

public sealed record ConversationResponse(string Id, string Object, long CreatedAt, string Status, Dictionary<string, object> Metadata);
