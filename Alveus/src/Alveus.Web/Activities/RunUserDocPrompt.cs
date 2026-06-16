using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="AgentPromptActivityBase.Prompt"/> à l'agent Alveus-UserDoc (par défaut
/// "AlveusUserDoc"), exécuté après <see cref="RunEvaluatorPrompt"/> dans le workflow (cf. ADR
/// 0026). Workspace dédié (<c>Agent:UserDocWorkspaceRoot</c>), incluant en sous-répertoires les
/// workspaces des agents spécialistes configurés (cf. ADR 0025/0030). Agent volontairement
/// minimal : <see cref="HandleDoneAsync"/> sort directement par "Done", sans vérification ADR 0020
/// (même schéma que <see cref="RunEvaluatorPrompt"/> sans verdict).
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à l'agent Alveus-UserDoc, qui met à jour la documentation utilisateur, et attend son issue (Done/NeedsMoreInfo/Blocked) via FinishTool.")]
public sealed class RunUserDocPrompt : AgentPromptActivityBase
{
    private const string DefaultAgentName = "AlveusUserDoc";

    public RunUserDocPrompt(IAgentSessionCompactionService compactionService)
        : base(compactionService, DefaultAgentName)
    {
    }

    protected override async ValueTask<string?> HandleDoneAsync(ActivityExecutionContext context, FinishCall finish)
    {
        await context.CompleteActivityWithOutcomesAsync(["Done"]);
        return null;
    }
}
