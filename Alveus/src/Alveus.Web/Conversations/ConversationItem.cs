namespace Alveus.Web.Conversations;

/// <summary>
/// Catégorie d'un <see cref="ConversationItem"/> — cf. ADR 0027. <see cref="UserMessage"/> et
/// <see cref="HumanReply"/> proviennent de l'utilisateur ; les autres sont postés par le workflow
/// pour observabilité (<see cref="ActivityTransition"/>, <see cref="FileEdit"/>,
/// <see cref="MeetingRound"/>) ou pour demander de l'aide (<see cref="NeedsHelpQuestion"/>).
/// <see cref="AssistantMessage"/> est réservé à une réponse finale éventuelle (non utilisé au
/// premier lot).
/// </summary>
public enum ConversationItemKind
{
    UserMessage,
    AssistantMessage,
    ActivityTransition,
    FileEdit,
    MeetingRound,
    NeedsHelpQuestion,
    HumanReply,
    ExpertQuestion,
    ExpertAnswer,
}

/// <summary>
/// Item d'une conversation au format OpenAI Conversations (sous-ensemble, cf. ADR 0027).
/// <paramref name="Metadata"/> porte des informations spécifiques à <paramref name="Kind"/> (ex.
/// <c>activityId</c> pour <see cref="ConversationItemKind.ActivityTransition"/>, <c>agent</c>/
/// <c>command</c>/<c>path</c> pour <see cref="ConversationItemKind.FileEdit"/>, <c>meeting</c>/
/// <c>round</c> pour <see cref="ConversationItemKind.MeetingRound"/>).
/// </summary>
public sealed record ConversationItem(
    string Id,
    string ConversationId,
    string Role,
    string Text,
    ConversationItemKind Kind,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);
