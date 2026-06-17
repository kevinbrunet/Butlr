using Alveus.Web.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Vérifie que <see cref="SkillsContextProvider"/> injecte le catalogue des skills (nom +
/// description du frontmatter) dans <see cref="AIContext.Instructions"/> — cf. ADR 0022.
/// Ne nécessite pas de llama.cpp : seule la lecture des fichiers <c>*.skill.md</c> est testée.
/// </summary>
public sealed class EvaluatorSkillsContextProviderTests : IDisposable
{
    private readonly string _skillsRoot = Directory.CreateTempSubdirectory("alveus-skills-context-tests-").FullName;
    private readonly AIAgent _agent = new ChatClientAgent(new NotImplementedChatClient(), new ChatClientAgentOptions { Name = "TestAgent" });

    [Fact]
    public async Task ProvideAIContextAsync_GivenSkillFiles_InjectsSkillCatalogInInstructions()
    {
        File.WriteAllText(Path.Combine(_skillsRoot, "verify.skill.md"),
            "---\nname: verify\ndescription: Snapshot testing API/JSON with Verify.\n---\n# Verify\n");
        File.WriteAllText(Path.Combine(_skillsRoot, "playwright.skill.md"),
            "---\nname: playwright\ndescription: Visual regression tests with Playwright.\n---\n# Playwright\n");

        var provider = new SkillsContextProvider(_skillsRoot, ["verify", "playwright"]);
        var aiContext = await InvokeAsync(provider);

        Assert.NotNull(aiContext.Instructions);
        Assert.Contains("verify", aiContext.Instructions);
        Assert.Contains("playwright", aiContext.Instructions);
        Assert.Contains("load_skill", aiContext.Instructions);
        Assert.Contains("Snapshot testing API/JSON with Verify.", aiContext.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_GivenEmptySkillList_ReturnsEmptyInstructions()
    {
        var provider = new SkillsContextProvider(_skillsRoot, []);
        var aiContext = await InvokeAsync(provider);

        Assert.Null(aiContext.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_GivenMissingSkillFile_SkipsIt()
    {
        File.WriteAllText(Path.Combine(_skillsRoot, "verify.skill.md"),
            "---\nname: verify\ndescription: Snapshot testing.\n---\n");

        var provider = new SkillsContextProvider(_skillsRoot, ["verify", "nonexistent"]);
        var aiContext = await InvokeAsync(provider);

        Assert.NotNull(aiContext.Instructions);
        Assert.Contains("verify", aiContext.Instructions);
        Assert.DoesNotContain("nonexistent", aiContext.Instructions);
    }

    private async Task<AIContext> InvokeAsync(SkillsContextProvider provider)
    {
        var session = await _agent.CreateSessionAsync();

#pragma warning disable MAAI001
        var invokingContext = new AIContextProvider.InvokingContext(_agent, session, new AIContext());
#pragma warning restore MAAI001

        return await provider.InvokingAsync(invokingContext);
    }

    public void Dispose()
    {
        Directory.Delete(_skillsRoot, recursive: true);
    }

    private sealed class NotImplementedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
