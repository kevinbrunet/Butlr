using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="AgentPromptActivityBase.Prompt"/> à l'agent évaluateur (par défaut
/// "AlveusEvaluator") — la même consigne de tâche que <see cref="RunAgentPrompt"/>, plus les
/// instructions d'utilisation de l'environnement local (cf. ADR 0023), mais dans un workspace
/// isolé (cf. ADR 0021). Le rôle de l'évaluateur est d'écrire un jeu de test à partir de cette
/// consigne, de l'exécuter contre l'environnement réel, et de rendre un verdict via
/// <see cref="AgentOutcome"/> sur le travail du Worker — pas d'effectuer la tâche lui-même.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à l'agent évaluateur, qui écrit et exécute un jeu de test contre l'environnement, et attend son verdict (Passed/Failed/NeedsMoreInfo) via FinishTool.")]
public sealed class RunEvaluatorPrompt : AgentPromptActivityBase
{
    private const string DefaultAgentName = "AlveusEvaluator";

    public RunEvaluatorPrompt(IAgentSessionCompactionService compactionService)
        : base(compactionService, DefaultAgentName)
    {
    }

    protected override async ValueTask<string?> HandlePassAsync(ActivityExecutionContext context, FinishCall finish)
    {
        context.Set(Reason, null);
        PostOutcome(context, $"{context.Activity.Id} → Passed");
        await context.CompleteActivityWithOutcomesAsync(["Passed"]);
        return null;
    }

    protected override async ValueTask<string?> HandleFailAsync(ActivityExecutionContext context, FinishCall finish)
    {
        context.Set(Reason, finish.Reason);
        PostOutcome(context, $"{context.Activity.Id} → Failed : {finish.Reason}");
        await context.CompleteActivityWithOutcomesAsync(["Failed"]);
        return null;
    }
}
