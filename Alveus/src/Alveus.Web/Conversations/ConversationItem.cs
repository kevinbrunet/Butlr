namespace Alveus.Web.Conversations;

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
