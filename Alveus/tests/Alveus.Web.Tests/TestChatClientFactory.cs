using System.ClientModel;
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
/// <item><see cref="FunctionInvokingChatClient"/> avec <c>MaximumIterationsPerRequest = 20</c> et
/// terminaison immédiate dès que <see cref="FinishTool"/> est appelé.</item>
/// </list>
/// </summary>
internal static class TestChatClientFactory
{
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
