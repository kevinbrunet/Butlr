namespace Alveus.Web.Conversations;

/// <summary>
/// Sous-ensemble du format OpenAI Conversations utilisé par <see cref="ConversationEndpoints"/> (cf.
/// ADR 0027) — API self-hosted, aucun appel à api.openai.com.
/// </summary>
public sealed record ConversationContentPart(string Type, string Text);

public sealed record ConversationItemRequest(string Role, List<ConversationContentPart> Content);

public sealed record CreateConversationRequest(List<ConversationItemRequest> Items);

public sealed record AddConversationItemsRequest(List<ConversationItemRequest> Items);

public sealed record ConversationResponse(string Id, string Object, long CreatedAt, string Status, Dictionary<string, object> Metadata);

/// <summary><c>Metadata["kind"]</c> ∈ <c>user_message | assistant_message | activity_transition | file_edit | meeting_round | needs_help_question | human_reply</c> (cf. <see cref="ConversationItemKind"/>).</summary>
public sealed record ConversationItemResponse(
    string Id,
    string Object,
    string Type,
    string Role,
    List<ConversationContentPart> Content,
    long CreatedAt,
    Dictionary<string, string> Metadata);

public sealed record ConversationItemListResponse(
    string Object,
    List<ConversationItemResponse> Data,
    bool HasMore,
    string? FirstId,
    string? LastId);
