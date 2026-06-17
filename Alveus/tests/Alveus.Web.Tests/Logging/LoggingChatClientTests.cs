using Alveus.Web.Conversations;
using Alveus.Web.Logging;
using Microsoft.Extensions.AI;
using AlveusLoggingChatClient = Alveus.Web.Logging.LoggingChatClient;

namespace Alveus.Web.Tests.Logging;

/// <summary>
/// Tests d'intégration de <see cref="LoggingChatClient"/> : vérifie que les échanges LLM sont
/// correctement diffusés au <see cref="IConversationStore"/> (items <c>ToolCall</c> et événements
/// <c>LlmExchange</c>), notamment les cas de régression :<br/>
/// - <see cref="TextReasoningContent"/> (thinking Qwen3) n'est pas silencieusement ignoré ;<br/>
/// - <see cref="FunctionCallContent"/> génère un item <see cref="ConversationItemKind.ToolCall"/>
///   dans le store (et non un chunk <c>reasoning_content</c> qui créerait un doublon).
/// </summary>
public sealed class LoggingChatClientTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class StubChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubContextAccessor : IConversationContextAccessor
    {
        public string? ConversationId { get; set; }
        public string? AgentName { get; set; }
    }

    private sealed class NullTaskLogger : ITaskLogger
    {
        public static readonly NullTaskLogger Instance = new();
        public void OnItem(ConversationItem item) { }
        public void OnLlmExchange(string conversationId, IEnumerable<ChatMessage> messages, ChatResponse response) { }
        public void OnCompleted(string conversationId, string status) { }
    }

    // ── Factory ───────────────────────────────────────────────────────────────────────────────────

    private static (AlveusLoggingChatClient client, ConversationStore store, string conversationId) Build(
        ChatResponse response, string? conversationId = null, string agentName = "TestAgent")
    {
        var store = new ConversationStore();
        var conv = store.Create();
        var ctxId = conversationId ?? conv.Id;
        var context = new StubContextAccessor { ConversationId = ctxId, AgentName = agentName };
        var client = new AlveusLoggingChatClient(new StubChatClient(response), NullTaskLogger.Instance, context, store);
        return (client, store, conv.Id);
    }

    // ── ToolCall items ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_WithFunctionCallContent_AddsToolCallItem()
    {
        var fc = new FunctionCallContent("call-1", "MyFunction",
            new Dictionary<string, object?> { ["param"] = "value" });
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc])]);
        var (client, store, convId) = Build(response);

        await client.GetResponseAsync([]);

        var items = store.GetItems(convId);
        var toolCall = Assert.Single(items, i => i.Kind == ConversationItemKind.ToolCall);
        Assert.Equal("MyFunction(param=value)", toolCall.Text);
        Assert.Equal("TestAgent", toolCall.Metadata["agent"]);
        Assert.Equal("MyFunction", toolCall.Metadata["tool"]);
    }

    [Fact]
    public async Task GetResponseAsync_WithFunctionCallContent_NoArguments_ToolCallTextIsJustName()
    {
        var fc = new FunctionCallContent("call-1", "Finish", null);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc])]);
        var (client, store, convId) = Build(response);

        await client.GetResponseAsync([]);

        var toolCall = Assert.Single(store.GetItems(convId), i => i.Kind == ConversationItemKind.ToolCall);
        Assert.Equal("Finish", toolCall.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WithMultipleFunctionCallContents_AddsOneToolCallItemEach()
    {
        var fc1 = new FunctionCallContent("call-1", "ToolA", null);
        var fc2 = new FunctionCallContent("call-2", "ToolB", null);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc1, fc2])]);
        var (client, store, convId) = Build(response);

        await client.GetResponseAsync([]);

        var toolCalls = store.GetItems(convId).Where(i => i.Kind == ConversationItemKind.ToolCall).ToList();
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("ToolA", toolCalls[0].Metadata["tool"]);
        Assert.Equal("ToolB", toolCalls[1].Metadata["tool"]);
    }

    [Fact]
    public async Task GetResponseAsync_WithoutConversationId_AddsNoItemsToStore()
    {
        var fc = new FunctionCallContent("call-1", "MyFunction", null);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc])]);
        var store = new ConversationStore();
        var conv = store.Create();
        var context = new StubContextAccessor { ConversationId = null };
        var client = new AlveusLoggingChatClient(new StubChatClient(response), NullTaskLogger.Instance, context, store);

        await client.GetResponseAsync([]);

        Assert.Empty(store.GetItems(conv.Id));
    }

    // ── LlmExchangeStreamEvent ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_PublishesLlmExchangeEvent()
    {
        var tc = new TextContent("Hello");
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [tc])]);
        var (client, store, convId) = Build(response, agentName: "Worker");

        var events = store.SubscribeEventsAsync(convId, CancellationToken.None);
        await client.GetResponseAsync([]);
        store.Complete(convId);

        LlmExchangeStreamEvent? llmEvent = null;
        await foreach (var evt in events)
        {
            if (evt is LlmExchangeStreamEvent e) { llmEvent = e; break; }
        }

        Assert.NotNull(llmEvent);
        Assert.Equal("Worker", llmEvent.AgentName);
        Assert.Equal(convId, llmEvent.ConversationId);
    }

    [Fact]
    public async Task GetResponseAsync_WithTextReasoningContent_ResponseInLlmExchangeEventContainsReasoning()
    {
        // Régression : TextReasoningContent (thinking Qwen3) ne doit pas être ignoré par
        // LoggingChatClient — il doit apparaître dans le ChatResponse du LlmExchangeStreamEvent.
        var trc = new TextReasoningContent("Je réfléchis en profondeur...");
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [trc])]);
        var (client, store, convId) = Build(response, agentName: "Thinker");

        var events = store.SubscribeEventsAsync(convId, CancellationToken.None);
        await client.GetResponseAsync([]);
        store.Complete(convId);

        LlmExchangeStreamEvent? llmEvent = null;
        await foreach (var evt in events)
        {
            if (evt is LlmExchangeStreamEvent e) { llmEvent = e; break; }
        }

        Assert.NotNull(llmEvent);
        Assert.Contains(
            llmEvent.Response.Messages.SelectMany(m => m.Contents),
            c => c is TextReasoningContent t && t.Text == "Je réfléchis en profondeur...");
    }

    [Fact]
    public async Task GetResponseAsync_AgentNameAppearsInToolCallMetadata()
    {
        var fc = new FunctionCallContent("call-1", "Run", null);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc])]);
        var (client, store, convId) = Build(response, agentName: "team:Worker");

        await client.GetResponseAsync([]);

        var toolCall = Assert.Single(store.GetItems(convId), i => i.Kind == ConversationItemKind.ToolCall);
        Assert.Equal("team:Worker", toolCall.Metadata["agent"]);
    }
}
