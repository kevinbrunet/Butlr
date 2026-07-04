using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;

namespace Alveus.Web.Workflows;

/// <summary>
/// Met en forme l'escalade "NeedsMoreInfo"/"Blocked" d'un agent (Worker, EnvironmentManager,
/// Evaluator ou UserDoc) dans <see cref="Report"/>, pour que <c>RunPreTaskMeeting</c> puisse en
/// tenir compte (cf. ADR 0028). Quatre instances dans <see cref="AlveusTaskWorkflow"/>, une par
/// agent source, partageant la même <see cref="Report"/>.
/// </summary>
[Activity("Alveus", "AI", "Met en forme l'escalade NeedsMoreInfo/Blocked d'un agent pour la réunion de pré-tâche.")]
public sealed class RecordAgentEscalation : CodeActivity
{
    /// <summary>Nom de l'agent à l'origine de l'escalade (ex. "Alveus-Worker").</summary>
    public required Input<string> SourceLabel { get; set; }

    /// <summary>Raison de l'escalade, si fournie par l'agent.</summary>
    public Input<string?> Reason { get; set; } = new(default(string?));

    /// <summary>Questions posées par l'agent, si fournies.</summary>
    public Input<IReadOnlyList<string>?> Questions { get; set; } = new(default(IReadOnlyList<string>?));

    /// <summary>Compte-rendu formaté de l'escalade, partagé entre les instances.</summary>
    public required Variable<string?> Report { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var source = context.Get(SourceLabel);
        var reason = context.Get(Reason);
        var questions = context.Get(Questions);

        var text = $"Escalade de {source}";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            text += $" : {reason}";
        }

        if (questions is { Count: > 0 })
        {
            text += "\nQuestions :\n" + string.Join("\n", questions.Select(q => $"- {q}"));
        }

        context.Set(Report, text, null);

        await context.CompleteActivityWithOutcomesAsync(["Done"]);
    }
}
