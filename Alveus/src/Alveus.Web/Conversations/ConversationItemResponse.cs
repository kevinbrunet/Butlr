namespace Alveus.Web.Conversations;

/// <summary><c>Metadata["kind"]</c> ∈ <c>user_message | assistant_message | activity_transition | file_edit | meeting_round | needs_help_question | human_reply</c> (cf. <see cref="ConversationItemKind"/>).</summary>
public sealed record ConversationItemResponse(
    string Id,
    string Object,
    string Type,
    string Role,
    List<ConversationContentPart> Content,
    long CreatedAt,
    Dictionary<string, string> Metadata);
