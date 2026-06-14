using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;

namespace Alveus.Web.Workflows;

/// <summary>
/// Garde-fou contre les boucles infinies de la réunion finale dans <see cref="AlveusTaskWorkflow"/>
/// (cf. ADR 0026) : incrémente <see cref="OuterLoopCount"/> et complète avec l'issue "Continue" tant
/// que <see cref="MaxIterations"/> n'est pas dépassé, sinon "LimitReached". Distinct de
/// <see cref="LoopIterationGuard"/> (cycle interne Worker → EnvironmentManager → Evaluator, ADR
/// 0023) : ici, un cycle borné est beaucoup plus coûteux (réunion de pré-tâche → cycle interne →
/// UserDoc → réunion finale), d'où une limite plus basse.
/// </summary>
[Activity("Alveus", "AI", "Incrémente un compteur de cycles réunion finale → réunion de pré-tâche et bascule vers \"LimitReached\" si la limite est dépassée.")]
public sealed class OuterLoopIterationGuard : CodeActivity
{
    /// <summary>Nombre maximal de cycles réunion finale → réunion de pré-tâche avant abandon.</summary>
    public const int MaxIterations = 3;

    /// <summary>Variable de compteur partagée avec le reste du workflow.</summary>
    public required Variable<int> OuterLoopCount { get; set; }

    /// <summary>Numéro du cycle qui vient de s'exécuter (premier cycle = 1).</summary>
    [Output(Description = "Numéro du cycle qui vient de s'exécuter (premier cycle = 1).")]
    public Output<int> Iteration { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var count = context.Get<int>(OuterLoopCount) + 1;
        context.Set(OuterLoopCount, count, null);
        context.Set(Iteration, count);

        await context.CompleteActivityWithOutcomesAsync([count > MaxIterations ? "LimitReached" : "Continue"]);
    }
}
