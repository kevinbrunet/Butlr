using System.ClientModel;
using System.Text;
using Alveus.Web.Tools;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Alveus.Web.Tests;

/// <summary>
/// Fabrique un <see cref="IChatClient"/> correctement configuré pour les tests d'intégration
/// (même pipeline que <c>Program.cs</c>) :
/// <list type="bullet">
/// <item><see cref="OpenAIClientOptions.NetworkTimeout"/> élevé (Qwen3 en mode thinking peut prendre
/// plusieurs minutes par appel).</item>
/// <item><see cref="NoThinkMiddleware"/> : injecte <c>/no_think</c> dans le system prompt pour
/// désactiver le mode thinking de Qwen3 côté client, indépendamment du proxy llama.</item>
/// <item><see cref="TestLlmLogger"/> : logue chaque échange LLM (messages + réponse) dans
/// <c>/tmp/alveus-test-llm-{timestamp}.log</c> pour faciliter le débogage des comportements
/// du modèle pendant les tests d'intégration.</item>
/// <item><see cref="FunctionInvokingChatClient"/> avec <c>MaximumIterationsPerRequest = 20</c> et
/// terminaison immédiate dès que <see cref="FinishTool"/> est appelé.</item>
/// </list>
/// </summary>
internal static class TestChatClientFactory
{
    public static readonly string LogPath = Path.Combine(
        Path.GetTempPath(),
        $"alveus-test-llm-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static IChatClient Create()
    {
        var openAiClient = new OpenAIClient(new ApiKeyCredential("not-needed"), new OpenAIClientOptions
        {
            Endpoint = new Uri(TestLlamaCppConfig.Endpoint ?? "http://not-configured"),
            NetworkTimeout = TimeSpan.FromMinutes(10),
        });

        IChatClient chatClient = openAiClient
            .GetChatClient(TestLlamaCppConfig.Model ?? "not-configured")
            .AsIChatClient();

        chatClient = new TestLlmLogger(chatClient);
        chatClient = new NoThinkMiddleware(chatClient);

        return new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = 20,
            FunctionInvoker = async (ctx, ct) =>
            {
                var result = await ctx.Function.InvokeAsync(ctx.Arguments, ct);
                if (ctx.Function.Name == FinishTool.FunctionName)
                    ctx.Terminate = true;
                return result;
            },
        };
    }

    /// <summary>
    /// Logue chaque échange LLM (messages envoyés + réponse reçue) dans <see cref="LogPath"/>.
    /// Positionné entre <see cref="NoThinkMiddleware"/> et le client OpenAI pour voir exactement
    /// ce que le modèle reçoit et renvoie. N'interrompt jamais les tests en cas d'erreur d'écriture.
    /// </summary>
    private sealed class TestLlmLogger(IChatClient inner) : DelegatingChatClient(inner)
    {
        private static int _callCounter;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            var response = await base.GetResponseAsync(messageList, options, cancellationToken);
            WriteLog(messageList, response);
            return response;
        }

        private static void WriteLog(IReadOnlyList<ChatMessage> messages, ChatResponse response)
        {
            try
            {
                var n = Interlocked.Increment(ref _callCounter);
                var sb = new StringBuilder();
                sb.AppendLine($"\n{'='  ,0}=== #{n} {DateTimeOffset.Now:HH:mm:ss.fff} ===");
                foreach (var msg in messages)
                {
                    var text = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));
                    var toolResults = string.Join("", msg.Contents.OfType<FunctionResultContent>()
                        .Select(fr => $"[tool_result] {fr.Result}"));
                    if (!string.IsNullOrEmpty(text))
                        sb.AppendLine($"[{msg.Role}] {text}");
                    if (!string.IsNullOrEmpty(toolResults))
                        sb.AppendLine($"[{msg.Role}] {toolResults}");
                }
                sb.AppendLine("--- RESPONSE ---");
                foreach (var msg in response.Messages)
                {
                    foreach (var content in msg.Contents)
                    {
                        switch (content)
                        {
                            case TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                                sb.AppendLine($"[text] {t.Text}");
                                break;
                            case FunctionCallContent fc:
                                var args = fc.Arguments is not null
                                    ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                                    : string.Empty;
                                sb.AppendLine($"[tool_call] {fc.Name}({args})");
                                break;
                        }
                    }
                }
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* ne jamais faire planter un test à cause du logging */ }
        }
    }

    /// <summary>
    /// Préfixe le system prompt de <c>/no_think</c> pour désactiver le mode thinking de Qwen3.
    /// Fonctionne indépendamment du proxy llama et de la version de llama.cpp.
    /// </summary>
    private sealed class NoThinkMiddleware(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetResponseAsync(WithNoThink(messages), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetStreamingResponseAsync(WithNoThink(messages), options, cancellationToken);

        private static IEnumerable<ChatMessage> WithNoThink(IEnumerable<ChatMessage> messages)
        {
            var copy = messages.ToList();
            var idx = copy.FindIndex(m => m.Role == ChatRole.System);
            if (idx < 0)
                return copy;

            var sys = copy[idx];
            var contents = sys.Contents.ToList();
            if (contents.FirstOrDefault() is TextContent first)
                contents[0] = new TextContent("/no_think\n" + first.Text);
            else
                contents.Insert(0, new TextContent("/no_think"));

            copy[idx] = new ChatMessage(ChatRole.System, contents);
            return copy;
        }
    }
}
