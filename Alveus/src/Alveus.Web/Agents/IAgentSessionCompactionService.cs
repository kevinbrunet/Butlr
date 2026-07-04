using Microsoft.Agents.AI;

namespace Alveus.Web.Agents;

/// <summary>
/// Compacte une <see cref="AgentSession"/> devenue trop volumineuse avant qu'elle ne soit
/// réinjectée dans l'appel suivant — cf. ADR 0019. Plusieurs stratégies (résumé via le LLM,
/// troncature, ...) pourront coexister derrière cette interface ;
/// <see cref="Alveus.Web.Activities.RunAgentPrompt"/> reçoit l'implémentation par injection de dépendances
/// à sa création, pour ne pas figer la stratégie dans l'activité elle-même.
/// </summary>
public interface IAgentSessionCompactionService
{
    /// <summary>
    /// Retourne <paramref name="session"/> inchangée si elle est sous le seuil de compactage,
    /// ou une session compactée équivalente sinon.
    /// </summary>
    ValueTask<AgentSession> CompactIfNeededAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken = default);
}
