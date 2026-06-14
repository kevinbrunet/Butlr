using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;

namespace Alveus.Web.Workflows;

/// <summary>
/// Garde-fou contre les boucles infinies d'aide humaine dans <see cref="AlveusTaskWorkflow"/> (cf.
/// ADR 0027) : incrémente <see cref="HelpLoopCount"/> et complète avec l'issue "Continue" tant que
/// <see cref="MaxIterations"/> n'est pas dépassé, sinon "LimitReached". Place après
/// <c>AwaitConversationReply</c> sur le chemin "NeedsHelp → réponse humaine → nouvelle tentative" des
/// réunions de pré-tâche et de revue finale. <see cref="MaxIterations"/> est volontairement plus
/// généreux que <see cref="LoopIterationGuard.MaxIterations"/> : la boucle est pilotée par un humain,
/// pas par des relances automatiques d'agent.
/// </summary>
[Activity("Alveus", "AI", "Incrémente un compteur de cycles d'aide humaine et bascule vers \"LimitReached\" si la limite est dépassée.")]
public sealed class HelpLoopIterationGuard : CodeActivity
{
    /// <summary>Nombre maximal de cycles "NeedsHelp → réponse humaine → nouvelle tentative" avant abandon.</summary>
    public const int MaxIterations = 5;

    /// <summary>Variable de compteur partagée avec le reste du workflow.</summary>
    public required Variable<int> HelpLoopCount { get; set; }

    /// <summary>Numéro du cycle qui vient de s'exécuter (premier cycle = 1).</summary>
    [Output(Description = "Numéro du cycle qui vient de s'exécuter (premier cycle = 1).")]
    public Output<int> Iteration { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var count = context.Get<int>(HelpLoopCount) + 1;
        context.Set(HelpLoopCount, count, null);
        context.Set(Iteration, count);

        await context.CompleteActivityWithOutcomesAsync([count > MaxIterations ? "LimitReached" : "Continue"]);
    }
}
