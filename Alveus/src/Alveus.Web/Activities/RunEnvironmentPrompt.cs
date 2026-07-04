using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="AgentPromptActivityBase.Prompt"/> à l'agent EnvironmentManager (par défaut
/// "AlveusEnvironmentManager") — exécuté après <see cref="RunAgentPrompt"/> dans le workflow
/// (cf. ADR 0023). Cet agent réutilise les tools et le workspace du Worker (ADR 0017), et a pour
/// rôle de lancer ou relancer l'environnement local décrit par la consigne, puis de rendre un
/// verdict via <see cref="AgentOutcome"/> : <c>Pass</c> si l'environnement est prêt,
/// <c>Fail</c> si le démarrage échoue, <c>NeedsMoreInfo</c> si la consigne ne précise pas
/// comment démarrer l'environnement.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à l'agent EnvironmentManager, qui (re)lance l'environnement local avec les tools/workspace du Worker, et attend son verdict (Done/Failed/NeedsMoreInfo) via FinishTool.")]
public sealed class RunEnvironmentPrompt : AgentPromptActivityBase
{
    private const string DefaultAgentName = "AlveusEnvironmentManager";

    public RunEnvironmentPrompt(IAgentSessionCompactionService compactionService)
        : base(compactionService, DefaultAgentName)
    {
    }

    protected override async ValueTask<string?> HandlePassAsync(ActivityExecutionContext context, FinishCall finish)
    {
        PostOutcome(context, $"{context.Activity.Id} → Done");
        await context.CompleteActivityWithOutcomesAsync(["Done"]);
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
