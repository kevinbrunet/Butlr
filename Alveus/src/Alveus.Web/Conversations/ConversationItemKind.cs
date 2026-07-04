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
    ToolCall,
}
