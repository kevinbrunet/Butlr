namespace Alveus.Web.Activities;

/// <summary>
/// Issue agrégée d'une réunion (hors sorties individuelles "NeedsMoreInfo"/"Blocked", traitées
/// directement par <see cref="MeetingActivityBase"/> — cf. ADR 0024) : <see cref="Done"/> si les 3
/// participants ont confirmé "Finish(done)" au même round sans topic ouvert, <see cref="NeedsHelp"/>
/// si <see cref="MeetingActivityBase.MaxRounds"/> est atteint sans consensus ou si un topic reste
/// 2 contre 1 après un round de correction.
/// </summary>
public enum MeetingOutcome
{
    Done,
    NeedsHelp,
}
