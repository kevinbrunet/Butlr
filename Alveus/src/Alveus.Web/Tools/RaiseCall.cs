namespace Alveus.Web.Tools;

/// <summary>
/// Appel à <see cref="MeetingTool.Raise"/> extrait de la réponse d'un participant à une
/// <see cref="Activities.MeetingActivityBase"/> (cf. ADR 0024).
/// </summary>
public sealed record RaiseCall(string Topic, string Comment)
{
    /// <summary>
    /// Construit un <see cref="RaiseCall"/> à partir des arguments bruts d'un appel de fonction.
    /// Retourne <c>null</c> si <paramref name="arguments"/> ne décrit pas un appel valide
    /// (topic ou comment absent).
    /// </summary>
    public static RaiseCall? FromArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var topic = MeetingCallArguments.ReadString(arguments, "topic");
        var comment = MeetingCallArguments.ReadString(arguments, "comment");
        if (topic is null || comment is null)
        {
            return null;
        }

        return new RaiseCall(topic, comment);
    }
}
