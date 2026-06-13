using System.Text.Json;

namespace Alveus.Web.Tools;

/// <summary>
/// Appel à <see cref="FinishTool.Finish"/> extrait de la réponse de l'agent (via les
/// <c>FunctionCallContent</c> de <c>AgentResponse.Messages</c>) — cf. ADR 0019.
/// <paramref name="Verdict"/> n'est renseigné que pour l'EnvironmentManager et l'Evaluator
/// (cf. ADR 0023) — toujours <c>null</c> pour Alveus-Worker.
/// </summary>
public sealed record FinishCall(AgentTaskOutcome Outcome, string Summary, string? Reason, IReadOnlyList<string>? Questions, AgentVerdict? Verdict)
{
    /// <summary>
    /// Construit un <see cref="FinishCall"/> à partir des arguments bruts d'un appel de fonction.
    /// Retourne <c>null</c> si <paramref name="arguments"/> ne décrit pas un appel valide
    /// (outcome absent ou inconnu).
    /// </summary>
    public static FinishCall? FromArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var outcomeRaw = ReadString(arguments, "outcome");
        if (outcomeRaw is null || !Enum.TryParse<AgentTaskOutcome>(outcomeRaw, ignoreCase: true, out var outcome))
        {
            return null;
        }

        var summary = ReadString(arguments, "summary") ?? string.Empty;
        var reason = ReadString(arguments, "reason");
        var questions = ReadStringList(arguments, "questions");

        var verdictRaw = ReadString(arguments, "verdict");
        AgentVerdict? verdict = verdictRaw is not null && Enum.TryParse<AgentVerdict>(verdictRaw, ignoreCase: true, out var parsedVerdict)
            ? parsedVerdict
            : null;

        return new FinishCall(outcome, summary, reason, questions, verdict);
    }

    private static string? ReadString(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value.ToString(),
        };
    }

    private static IReadOnlyList<string>? ReadStringList(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            return element.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        }

        if (value is IEnumerable<object?> enumerable)
        {
            return enumerable.Select(o => o?.ToString() ?? string.Empty).ToList();
        }

        return null;
    }
}
