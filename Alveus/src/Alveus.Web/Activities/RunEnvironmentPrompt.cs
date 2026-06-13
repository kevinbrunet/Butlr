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
/// verdict (<see cref="AgentVerdict"/>) : "Pass" si l'environnement est prêt
/// (<see cref="AgentPromptActivityBase.Summary"/> contient alors les instructions d'utilisation
/// pour l'Evaluator), "Fail" si le démarrage échoue, "NeedMoreInfo" si la consigne ne précise pas
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

    protected override ValueTask<string?> HandleDoneAsync(ActivityExecutionContext context, FinishCall finish)
        => HandleVerdictAsync(context, finish, passOutcome: "Done");
}
