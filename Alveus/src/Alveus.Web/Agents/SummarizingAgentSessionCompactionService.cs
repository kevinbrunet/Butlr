using System.Text;
using Microsoft.Agents.AI;

namespace Alveus.Web.Agents;

/// <summary>
/// Stratégie de compactage par défaut (cf. ADR 0019) : si la session sérialisée dépasse le seuil
/// configuré, demande à l'agent de résumer la conversation
/// puis repart d'une session neuve amorcée avec ce résumé. Évite toute hypothèse sur le format
/// JSON interne de <see cref="AgentSession"/> (opaque), au prix de deux appels LLM
/// supplémentaires lors d'un compactage.
/// </summary>
public sealed class SummarizingAgentSessionCompactionService : IAgentSessionCompactionService
{
    private const int DefaultMaxSerializedSessionSizeBytes = 32_000;

    private const string SummarizePrompt =
        "Résume en quelques phrases la conversation précédente : objectif de la tâche, actions déjà effectuées, "
        + "résultats obtenus et état actuel. Ce résumé remplacera l'historique complet comme seul contexte pour la suite.";

    private readonly int _maxSerializedSessionSizeBytes;

    public SummarizingAgentSessionCompactionService(int maxSerializedSessionSizeBytes = DefaultMaxSerializedSessionSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSerializedSessionSizeBytes);
        _maxSerializedSessionSizeBytes = maxSerializedSessionSizeBytes;
    }

    public async ValueTask<AgentSession> CompactIfNeededAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken = default)
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) <= _maxSerializedSessionSizeBytes)
        {
            return session;
        }

        var summaryResponse = await agent.RunAsync(SummarizePrompt, session, cancellationToken: cancellationToken);

        var compactedSession = await agent.CreateSessionAsync(cancellationToken);
        await agent.RunAsync(
            $"Contexte (résumé de la conversation précédente) : {summaryResponse.Text}",
            compactedSession,
            cancellationToken: cancellationToken);

        return compactedSession;
    }
}
