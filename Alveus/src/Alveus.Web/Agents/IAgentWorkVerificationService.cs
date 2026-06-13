namespace Alveus.Web.Agents;

/// <summary>
/// Vérifie que le travail annoncé comme terminé (FinishTool, outcome "done") est effectivement
/// correct, avant que <see cref="Alveus.Web.Activities.RunAgentPrompt"/> ne sorte par l'issue
/// "Done" — cf. ADR 0020. Plusieurs stratégies (script de validation, appel à un autre agent, ...)
/// pourront coexister derrière cette interface ; <see cref="Alveus.Web.Activities.RunAgentPrompt"/>
/// reçoit l'implémentation par injection de dépendances à sa création, pour ne pas figer la
/// stratégie dans l'activité elle-même.
/// </summary>
public interface IAgentWorkVerificationService
{
    /// <summary>
    /// Vérifie le travail accompli. En cas d'échec, <see cref="AgentWorkVerificationResult.Output"/>
    /// contient les détails à réinjecter dans la conversation pour que l'agent corrige.
    /// </summary>
    ValueTask<AgentWorkVerificationResult> VerifyAsync(CancellationToken cancellationToken = default);
}
