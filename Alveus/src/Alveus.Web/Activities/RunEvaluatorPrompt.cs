using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="AgentPromptActivityBase.Prompt"/> à l'agent évaluateur (par défaut
/// "AlveusEvaluator") — la même consigne de tâche que <see cref="RunAgentPrompt"/>, mais dans un
/// workspace isolé (cf. ADR 0021). Le rôle de l'évaluateur est d'écrire, à partir de cette
/// consigne, un jeu de test permettant de vérifier objectivement qu'un travail y répondant est
/// correct — pas d'effectuer la tâche lui-même. Contrairement à <see cref="RunAgentPrompt"/>,
/// l'issue "Done" ne déclenche aucune vérification : l'évaluateur n'est pas le travail à vérifier.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à l'agent évaluateur, qui écrit un jeu de test dans son propre workspace, et attend son verdict via FinishTool.")]
public sealed class RunEvaluatorPrompt : AgentPromptActivityBase
{
    private const string DefaultAgentName = "AlveusEvaluator";

    public RunEvaluatorPrompt(IAgentSessionCompactionService compactionService)
        : base(compactionService, DefaultAgentName)
    {
    }

    protected override async ValueTask<string?> HandleDoneAsync(ActivityExecutionContext context, FinishCall finish)
    {
        await context.CompleteActivityWithOutcomesAsync(["Done"]);
        return null;
    }
}
