namespace Alveus.Web.Conversations;

public sealed record ConversationItemListResponse(
    string Object,
    List<ConversationItemResponse> Data,
    bool HasMore,
    string? FirstId,
    string? LastId);
