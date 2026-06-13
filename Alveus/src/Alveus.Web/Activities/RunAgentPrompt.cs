using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Alveus.Web.Activities;

/// <summary>
/// Envoie <see cref="AgentPromptActivityBase.Prompt"/> à l'agent désigné par
/// <see cref="AgentPromptActivityBase.AgentName"/> (par défaut "AlveusWorker") et attend que
/// l'agent appelle <see cref="Alveus.Web.Tools.FinishTool"/> pour se terminer — cf. ADR 0019.
/// Avant de sortir par l'issue "Done", le travail est vérifié par
/// <see cref="IAgentWorkVerificationService"/> (cf. ADR 0020) ; en cas d'échec, la boucle continue
/// avec le détail de l'échec en guise de prompt.
/// </summary>
[Activity("Alveus", "AI", "Envoie un prompt à un agent et attend son verdict (terminé, besoin d'informations, ou bloqué) via FinishTool.")]
public sealed class RunAgentPrompt : AgentPromptActivityBase
{
    private const string DefaultAgentName = "AlveusWorker";

    private const string VerificationFailedPrompt =
        "La vérification automatique du travail a échoué. Corrige le problème décrit ci-dessous, puis rappelle "
        + "l'outil Finish.\n\n";

    private readonly IAgentWorkVerificationService _workVerificationService;

    public RunAgentPrompt(IAgentSessionCompactionService compactionService, IAgentWorkVerificationService workVerificationService)
        : base(compactionService, DefaultAgentName)
    {
        ArgumentNullException.ThrowIfNull(workVerificationService);
        _workVerificationService = workVerificationService;
    }

    protected override async ValueTask<string?> HandleDoneAsync(ActivityExecutionContext context, FinishCall finish)
    {
        var verification = await _workVerificationService.VerifyAsync(context.CancellationToken);
        if (verification.Success)
        {
            await context.CompleteActivityWithOutcomesAsync(["Done"]);
            return null;
        }

        return VerificationFailedPrompt + verification.Output;
    }
}
