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
/// <item><see cref="FunctionInvokingChatClient"/> avec <c>MaximumIterationsPerRequest = 20</c> et
/// terminaison immédiate dès que <see cref="FinishTool"/> est appelé — sans ça, le LLM est
/// réinvoqué avec le résultat de Finish et rappelle Finish indéfiniment.</item>
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
}
