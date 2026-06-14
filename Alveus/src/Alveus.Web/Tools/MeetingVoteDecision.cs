namespace Alveus.Web.Tools;

/// <summary>
/// Décision exprimée par un agent via <see cref="MeetingTool.Vote"/> sur un topic de réunion
/// (cf. ADR 0024).
/// </summary>
public enum MeetingVoteDecision
{
    Agree,
    Disagree,
}
