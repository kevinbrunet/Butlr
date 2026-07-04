using System.ComponentModel;

namespace Alveus.Web.Tools;

/// <summary>
/// Outil de débat/vote exposé aux participants (Alveus-BusinessAnalyst, Alveus-Qa,
/// Alveus-Technical) d'une <see cref="Activities.MeetingActivityBase"/> — cf. ADR 0024. Distinct
/// de <see cref="FinishTool"/> : <see cref="FinishTool.Finish"/> signale la fin d'un tour pour
/// l'agent appelant, tandis que <see cref="Raise"/>/<see cref="Vote"/> coordonnent les 3
/// participants entre eux sur un sujet donné (<c>topic</c>).
/// </summary>
public sealed class MeetingTool
{
    /// <summary>Nom de la fonction exposée pour <see cref="Raise"/>.</summary>
    public const string RaiseFunctionName = "Raise";

    /// <summary>Nom de la fonction exposée pour <see cref="Vote"/>.</summary>
    public const string VoteFunctionName = "Vote";

    [Description("Signale un point de désaccord ou une question aux 2 autres participants de la réunion — visible "
        + "par eux à leur prochain tour. Utilise un 'topic' court et stable (réutilisé par Vote).")]
    public string Raise(
        [Description("Identifiant court et stable du sujet soulevé (ex. 'format-date-facture').")] string topic,
        [Description("Description du désaccord ou de la question.")] string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);

        return "Point signalé aux autres participants.";
    }

    [Description("Vote sur un topic signalé par Raise (ou sur le topic implicite 'task-fulfilled' en réunion finale). "
        + "decision='agree' ou 'disagree' ; comment obligatoire si 'disagree'.")]
    public string Vote(
        [Description("Identifiant du topic sur lequel voter (cf. Raise, ou 'task-fulfilled' en réunion finale).")] string topic,
        [Description("'agree' ou 'disagree'.")] string decision,
        [Description("Obligatoire si decision='disagree' : explique ton désaccord.")] string? comment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);

        if (!Enum.TryParse<MeetingVoteDecision>(decision, ignoreCase: true, out var parsedDecision))
        {
            throw new ArgumentException($"decision inconnue : '{decision}'. Attendu : agree ou disagree.", nameof(decision));
        }

        if (parsedDecision == MeetingVoteDecision.Disagree && string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("comment est obligatoire si decision='disagree'.", nameof(comment));
        }

        return "Vote enregistré.";
    }
}
