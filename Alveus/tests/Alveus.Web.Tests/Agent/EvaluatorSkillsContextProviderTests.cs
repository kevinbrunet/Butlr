using Alveus.Web.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Vérifie que <see cref="EvaluatorSkillsContextProvider"/> injecte le contenu des
/// <c>SKILL.md</c> dans <see cref="AIContext.Instructions"/> — cf. ADR 0022. Ne nécessite pas de
/// llama.cpp : seule la lecture des fichiers du workspace est testée.
/// </summary>
public sealed class EvaluatorSkillsContextProviderTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("alveus-evaluator-skills-context-tests-").FullName;
    private readonly AIAgent _agent = new ChatClientAgent(new NotImplementedChatClient(), new ChatClientAgentOptions { Name = "TestAgent" });

    [Fact]
    public async Task ProvideAIContextAsync_GivenSkillsDirectory_InjectsSkillContentInInstructions()
    {
        EvaluatorSkills.CopyInto(_workspaceRoot, AppContext.BaseDirectory);
        var provider = new EvaluatorSkillsContextProvider(_workspaceRoot);

        var aiContext = await InvokeAsync(provider);

        Assert.NotNull(aiContext.Instructions);
        Assert.Contains(EvaluatorSkills.DotnetSnapshotTestingSkillName, aiContext.Instructions);
        Assert.Contains("Snapshot / Approval Testing", aiContext.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_GivenNoSkillsDirectory_ReturnsEmptyInstructions()
    {
        var provider = new EvaluatorSkillsContextProvider(_workspaceRoot);

        var aiContext = await InvokeAsync(provider);

        Assert.Null(aiContext.Instructions);
    }

    private async Task<AIContext> InvokeAsync(EvaluatorSkillsContextProvider provider)
    {
        var session = await _agent.CreateSessionAsync();

#pragma warning disable MAAI001 // InvokingContext est expérimental (Microsoft.Agents.AI 1.10.0).
        var invokingContext = new AIContextProvider.InvokingContext(_agent, session, new AIContext());
#pragma warning restore MAAI001

        return await provider.InvokingAsync(invokingContext);
    }

    public void Dispose()
    {
        Directory.Delete(_workspaceRoot, recursive: true);
    }

    private sealed class NotImplementedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
