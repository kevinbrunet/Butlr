using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;

namespace Alveus.Web.Workflows;

/// <summary>
/// Garde-fou contre les boucles infinies dans <see cref="AlveusTaskWorkflow"/> (cf. ADR 0023) :
/// incrémente <see cref="LoopCount"/> et complète avec l'issue "Continue" tant que
/// <see cref="MaxIterations"/> n'est pas dépassé, sinon "LimitReached". Distinct du
/// <c>MaxIterations</c> interne à chaque <see cref="Activities.AgentPromptActivityBase"/>
/// (ADR 0019), qui borne les relances d'un seul agent au sein d'une activité.
/// </summary>
[Activity("Alveus", "AI", "Incrémente un compteur de cycles Worker/Evaluator et bascule vers \"LimitReached\" si la limite est dépassée.")]
public sealed class LoopIterationGuard : CodeActivity
{
    /// <summary>Nombre maximal de cycles Worker → EnvironmentManager → Evaluator avant abandon.</summary>
    public const int MaxIterations = 5;

    /// <summary>Variable de compteur partagée avec le reste du workflow.</summary>
    public required Variable<int> LoopCount { get; set; }

    /// <summary>Numéro du cycle qui vient de s'exécuter (premier cycle = 1).</summary>
    [Output(Description = "Numéro du cycle qui vient de s'exécuter (premier cycle = 1).")]
    public Output<int> Iteration { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var count = context.Get<int>(LoopCount) + 1;
        context.Set(LoopCount, count, null);
        context.Set(Iteration, count);

        await context.CompleteActivityWithOutcomesAsync([count > MaxIterations ? "LimitReached" : "Continue"]);
    }
}
