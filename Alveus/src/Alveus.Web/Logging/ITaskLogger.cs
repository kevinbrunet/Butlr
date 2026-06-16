using Alveus.Web.Conversations;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Logging;

/// <summary>
/// Observabilité des tâches Alveus — reçoit chaque <see cref="ConversationItem"/> produit par le
/// workflow, chaque échange LLM brut (thinking inclus), et la notification de fin de conversation.
/// L'implémentation par défaut écrit des fichiers markdown par activité (cf. <see cref="FileTaskLogger"/>).
/// </summary>
public interface ITaskLogger
{
    /// <summary>Appelé à chaque <see cref="IConversationStore.AddItem"/>.</summary>
    void OnItem(ConversationItem item);

    /// <summary>Appelé après chaque appel LLM (<c>GetResponseAsync</c>), thinking inclus.</summary>
    void OnLlmExchange(string conversationId, IEnumerable<ChatMessage> messages, ChatResponse response);

    /// <summary>Appelé à chaque <see cref="IConversationStore.Complete"/>.</summary>
    void OnCompleted(string conversationId, string status);
}
