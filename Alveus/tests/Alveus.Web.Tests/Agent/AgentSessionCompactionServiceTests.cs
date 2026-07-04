using Alveus.Web.Agents;
using Xunit.Abstractions;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Tests de <see cref="SummarizingAgentSessionCompactionService"/> (cf. ADR 0019). Le cas
/// "session sous le seuil" ne nécessite pas de serveur llama.cpp (pas d'appel LLM). Le cas
/// "compactage déclenché" en a besoin et est sauté si indisponible.
/// </summary>
public sealed class AgentSessionCompactionServiceTests : IClassFixture<AgentFixture>
{
    private readonly AgentFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AgentSessionCompactionServiceTests(AgentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task CompactIfNeededAsync_SessionUnderThreshold_ReturnsSameSession()
    {
        var service = new SummarizingAgentSessionCompactionService();
        var session = await _fixture.Agent.CreateSessionAsync();

        var result = await service.CompactIfNeededAsync(_fixture.Agent, session);

        Assert.Same(session, result);
    }

    [Fact]
    public async Task CompactIfNeededAsync_SessionOverThreshold_ReturnsDifferentSession()
    {
        if (!_fixture.IsLlamaCppAvailable)
        {
            _output.WriteLine("llama.cpp indisponible sur ALVEUS_TEST_LLAMACPP_ENDPOINT — test ignoré.");
            return;
        }

        // Seuil volontairement très bas pour forcer le compactage dès la première réponse.
        var service = new SummarizingAgentSessionCompactionService(maxSerializedSessionSizeBytes: 1);
        var session = await _fixture.Agent.CreateSessionAsync();
        var task = _fixture.Agent.RunAsync("Dis bonjour en une phrase.", session);
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(3))) == task)
            await task;

        var result = await service.CompactIfNeededAsync(_fixture.Agent, session);

        Assert.NotSame(session, result);
    }
}
