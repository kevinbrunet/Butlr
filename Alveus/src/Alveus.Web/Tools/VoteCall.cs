namespace Alveus.Web.Tools;

/// <summary>
/// Appel à <see cref="MeetingTool.Vote"/> extrait de la réponse d'un participant à une
/// <see cref="Activities.MeetingActivityBase"/> (cf. ADR 0024).
/// </summary>
public sealed record VoteCall(string Topic, MeetingVoteDecision Decision, string? Comment)
{
    /// <summary>
    /// Construit un <see cref="VoteCall"/> à partir des arguments bruts d'un appel de fonction.
    /// Retourne <c>null</c> si <paramref name="arguments"/> ne décrit pas un appel valide
    /// (topic ou decision absent/inconnu).
    /// </summary>
    public static VoteCall? FromArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var topic = MeetingCallArguments.ReadString(arguments, "topic");
        var decisionRaw = MeetingCallArguments.ReadString(arguments, "decision");
        if (topic is null || decisionRaw is null || !Enum.TryParse<MeetingVoteDecision>(decisionRaw, ignoreCase: true, out var decision))
        {
            return null;
        }

        var comment = MeetingCallArguments.ReadString(arguments, "comment");

        return new VoteCall(topic, decision, comment);
    }
}
