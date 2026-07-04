using System.Text.Json;
using Alveus.Web.Tools;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Tools;

/// <summary>
/// Vérifie que <see cref="FinishTool"/> interrompt la boucle tool-calling de
/// <see cref="FunctionInvokingChatClient"/> après le premier appel — comportement garanti par le
/// <c>FunctionInvoker</c> configuré dans <c>Program.cs</c> qui pose
/// <see cref="FunctionInvocationContext.Terminate"/> = <c>true</c> dès que Finish est exécuté.
/// Sans cette garde, le LLM est réinvoqué jusqu'à <c>MaximumIterationsPerRequest</c> à chaque
/// fois que Finish retourne un résultat, produisant des répétitions dans le stream OAI.
/// </summary>
public sealed class FinishToolLoopGuardTests
{
    /// <summary>
    /// Garantit que <see cref="FinishTool.FunctionName"/> correspond au nom réel de la fonction
    /// produite par <see cref="AIFunctionFactory.Create"/> — si la méthode est renommée sans
    /// mettre à jour la constante, le <c>FunctionInvoker</c> ne reconnaîtra plus Finish et la
    /// boucle ne s'arrêtera plus.
    /// </summary>
    [Fact]
    public void FinishTool_FunctionName_MatchesAIFunctionName()
    {
        var tool = new FinishTool();
        var function = AIFunctionFactory.Create(tool.Finish);

        Assert.Equal(FinishTool.FunctionName, function.Name);
    }

    /// <summary>
    /// Reproduit le bug original : sans <c>FunctionInvoker</c>, <see cref="FunctionInvokingChatClient"/>
    /// réinvoque le LLM après chaque exécution de Finish car il ajoute le résultat à l'historique
    /// et relance une complétion. Le LLM répond à nouveau avec Finish, et ainsi de suite jusqu'au
    /// plafond d'itérations.
    /// </summary>
    [Fact]
    public async Task FunctionInvokingChatClient_WithoutFinishInvoker_LlmCalledMultipleTimes()
    {
        var callCount = 0;
        var stub = new StubChatClient(_ =>
        {
            callCount++;
            return BuildFinishResponse();
        });

        var tool = new FinishTool();
        var client = new FunctionInvokingChatClient(stub) { MaximumIterationsPerRequest = 3 };
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(tool.Finish)] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        Assert.True(callCount > 1,
            $"Sans FunctionInvoker, le LLM doit être rappelé après Finish (appels observés : {callCount}).");
    }

    /// <summary>
    /// Vérifie que le <c>FunctionInvoker</c> de <c>Program.cs</c> — qui pose
    /// <see cref="FunctionInvocationContext.Terminate"/> = <c>true</c> sur Finish — arrête la
    /// boucle après un seul appel LLM, éliminant les répétitions dans le stream OAI.
    /// </summary>
    [Fact]
    public async Task FunctionInvokingChatClient_WithFinishInvoker_LlmCalledExactlyOnce()
    {
        var callCount = 0;
        var stub = new StubChatClient(_ =>
        {
            callCount++;
            return BuildFinishResponse();
        });

        var tool = new FinishTool();
        var client = new FunctionInvokingChatClient(stub)
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
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(tool.Finish)] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        Assert.Equal(1, callCount);
    }

    private static ChatResponse BuildFinishResponse()
        => new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent(
                    callId: "call-finish",
                    name: FinishTool.FunctionName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["summary"] = JsonSerializer.SerializeToElement("Tâche terminée."),
                        ["outcome"] = JsonSerializer.SerializeToElement("done"),
                    }),
            ]),
        ]);

    private sealed class StubChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(messages));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
