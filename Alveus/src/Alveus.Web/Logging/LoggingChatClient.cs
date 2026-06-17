using Alveus.Web.Conversations;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Logging;

/// <summary>
/// Decorator <see cref="IChatClient"/> qui logue chaque échange LLM brut (messages entrants +
/// réponse complète, thinking Qwen3 inclus) via <see cref="ITaskLogger.OnLlmExchange"/> ET le
/// diffuse en temps réel aux abonnés du flux OAI via
/// <see cref="IConversationStore.PublishLlmExchange"/>. La conversation courante et le nom de
/// l'agent actif sont résolus via <see cref="IConversationContextAccessor"/> — si l'identifiant de
/// conversation est vide (appel hors contexte workflow), l'échange n'est pas traité.
/// </summary>
public sealed class LoggingChatClient(
    IChatClient inner,
    ITaskLogger logger,
    IConversationContextAccessor contextAccessor,
    IConversationStore store) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);

        var conversationId = contextAccessor.ConversationId;
        if (!string.IsNullOrEmpty(conversationId))
        {
            logger.OnLlmExchange(conversationId, messages, response);
            store.PublishLlmExchange(conversationId, contextAccessor.AgentName ?? "?", response);

            var agentName = contextAccessor.AgentName ?? "?";
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        var argsText = fc.Arguments is not null
                            ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : string.Empty;
                        store.AddItem(
                            conversationId,
                            "assistant",
                            string.IsNullOrEmpty(argsText) ? fc.Name : $"{fc.Name}({argsText})",
                            ConversationItemKind.ToolCall,
                            new Dictionary<string, string> { ["agent"] = agentName, ["tool"] = fc.Name });
                    }
                }
            }
        }

        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(LoggingChatClient) ? this : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
