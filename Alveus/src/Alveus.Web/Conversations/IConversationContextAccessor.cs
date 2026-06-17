namespace Alveus.Web.Conversations;

/// <summary>
/// Contexte ambiant pour le thread d'exécution courant (cf. ADR 0027) — identifiant de
/// conversation et nom d'agent actif, propagés via <see cref="System.Threading.AsyncLocal{T}"/>
/// pour traverser les appels async sans passer par les signatures exposées au LLM.
/// </summary>
public interface IConversationContextAccessor
{
    string? ConversationId { get; set; }

    /// <summary>
    /// Nom de l'agent en cours d'exécution (clé DI, ex. <c>"MyTeam:Worker"</c>), utilisé pour
    /// annoter les échanges LLM dans le flux OAI (<see cref="WorkflowStreamEvent"/>).
    /// </summary>
    string? AgentName { get; set; }
}
