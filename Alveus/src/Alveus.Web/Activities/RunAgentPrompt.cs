using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;

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

    public RunAgentPrompt(IAgentSessionCompactionService compactionService)
        : base(compactionService, DefaultAgentName)
    {
    }

    protected override async ValueTask<string?> HandlePassAsync(ActivityExecutionContext context, FinishCall finish)
    {
        // Résolution tardive : essaie d'abord la clé équipe, se rabat sur le service non-keyed (tests, usage isolé).
        var sp = context.GetRequiredService<IServiceProvider>();
        var teamName = context.Get(TeamName);
        var verificationService = (!string.IsNullOrEmpty(teamName)
            ? sp.GetKeyedService<IAgentWorkVerificationService>(teamName)
            : null)
            ?? sp.GetService<IAgentWorkVerificationService>();

        if (verificationService is null)
        {
            PostOutcome(context, $"{context.Activity.Id} → Done");
            await context.CompleteActivityWithOutcomesAsync(["Done"]);
            return null;
        }

        var verification = await verificationService.VerifyAsync(context.CancellationToken);
        if (verification.Success)
        {
            PostOutcome(context, $"{context.Activity.Id} → Done (vérification OK)");
            await context.CompleteActivityWithOutcomesAsync(["Done"]);
            return null;
        }

        return VerificationFailedPrompt + verification.Output;
    }
}
