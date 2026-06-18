using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Agents;

/// <summary>
/// Stratégie de compactage par défaut (cf. ADR 0019) : si la session sérialisée dépasse le seuil
/// configuré, résume uniquement la conversation intermédiaire en préservant le prompt système
/// (injecté automatiquement par l'agent) et le premier message utilisateur (entrée du workflow).
/// </summary>
public sealed class SummarizingAgentSessionCompactionService : IAgentSessionCompactionService
{
    private const int DefaultMaxSerializedSessionSizeBytes = 32_000;

    private const string SummarizePrompt =
        "Résume en quelques phrases la conversation ci-dessus — sans utiliser aucun outil, en texte brut uniquement : "
        + "actions effectuées, résultats obtenus et état actuel. "
        + "Ce résumé remplacera l'historique de la conversation comme seul contexte pour la suite.";

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
            return session;

        // Accès à l'historique en mémoire ; dégradation gracieuse si indisponible.
        if (!session.TryGetInMemoryChatHistory(out var allMessages) || allMessages is null)
            return session;

        // Ancre = messages système + premier message utilisateur (entrée du workflow) — toujours préservés.
        var firstUserIndex = allMessages.FindIndex(m => m.Role == ChatRole.User);
        if (firstUserIndex < 0 || firstUserIndex >= allMessages.Count - 1)
            return session;

        var anchors = allMessages.Take(firstUserIndex + 1).ToList();
        var conversationToCompact = allMessages.Skip(firstUserIndex + 1).ToList();

        // Résumé uniquement de la conversation après l'entrée du workflow.
        var tempSession = await agent.CreateSessionAsync(cancellationToken);
        tempSession.SetInMemoryChatHistory(conversationToCompact);
        var summaryResponse = await agent.RunAsync(SummarizePrompt, tempSession, cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(summaryResponse.Text))
            return session; // L'agent a utilisé des outils au lieu de répondre en texte — on diffère au prochain tour.

        var compactedSession = await agent.CreateSessionAsync(cancellationToken);
        compactedSession.SetInMemoryChatHistory([
            .. anchors,
            new ChatMessage(ChatRole.Assistant, "[REPRISE — conversation précédente résumée] " + summaryResponse.Text),
        ]);

        return compactedSession;
    }
}
