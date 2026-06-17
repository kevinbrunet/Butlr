using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;

namespace Alveus.Web.Workflows;

/// <summary>
/// Garde-fou contre les boucles infinies du cycle escalade agent → réunion de pré-tâche dans
/// <see cref="AlveusTaskWorkflow"/> (cf. ADR 0028) : incrémente <see cref="AgentEscalationLoopCount"/>
/// et complète avec l'issue "Continue" tant que <see cref="MaxIterations"/> n'est pas dépassé, sinon
/// "LimitReached". Distinct de <see cref="OuterLoopIterationGuard"/> (cycle réunion finale → réunion
/// de pré-tâche, ADR 0026) : budget dédié, pour ne pas mélanger les deux causes de retour à
/// <c>RunPreTaskMeeting</c>.
/// </summary>
[Activity("Alveus", "AI", "Incrémente un compteur de cycles escalade agent → réunion de pré-tâche et bascule vers \"LimitReached\" si la limite est dépassée.")]
public sealed class AgentEscalationLoopGuard : CodeActivity
{
    /// <summary>Nombre maximal de cycles escalade agent → réunion de pré-tâche avant abandon.</summary>
    public const int MaxIterations = 1;

    /// <summary>Variable de compteur partagée avec le reste du workflow.</summary>
    public required Variable<int> AgentEscalationLoopCount { get; set; }

    /// <summary>Numéro du cycle qui vient de s'exécuter (premier cycle = 1).</summary>
    [Output(Description = "Numéro du cycle qui vient de s'exécuter (premier cycle = 1).")]
    public Output<int> Iteration { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var count = context.Get<int>(AgentEscalationLoopCount) + 1;
        context.Set(AgentEscalationLoopCount, count, null);
        context.Set(Iteration, count);

        await context.CompleteActivityWithOutcomesAsync([count > MaxIterations ? "LimitReached" : "Continue"]);
    }
}
