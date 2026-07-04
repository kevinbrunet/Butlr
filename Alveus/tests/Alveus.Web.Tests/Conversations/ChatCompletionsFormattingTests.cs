using Alveus.Web.Conversations;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Conversations;

/// <summary>
/// Tests de non-régression sur le formatage SSE de <see cref="ChatCompletionsEndpoints"/> :<br/>
/// - <see cref="ConversationItem"/> → texte contenu (<c>FormatItem</c>) ;<br/>
/// - <see cref="LlmExchangeStreamEvent"/> → chunks <c>reasoning_content</c>
///   (<c>FormatLlmExchangeReasoningChunks</c>).<br/>
/// Bugs couverts :<br/>
/// 1. <see cref="TextReasoningContent"/> silencieusement ignoré (thinking Qwen3 invisible) ;<br/>
/// 2. <see cref="FunctionCallContent"/> inclus dans <c>reasoning_content</c> (doublon avec item
///    <see cref="ConversationItemKind.ToolCall"/>).
/// </summary>
public sealed class ChatCompletionsFormattingTests
{
    private static ConversationItem MakeItem(
        ConversationItemKind kind,
        string text,
        Dictionary<string, string>? meta = null)
        => new(
            Guid.NewGuid().ToString("N"),
            "conv-test",
            "assistant",
            text,
            kind,
            meta ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

    // ── FormatItem ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatItem_ActivityTransitionStarting_ReturnsActivityHeader()
    {
        var item = MakeItem(ConversationItemKind.ActivityTransition, "",
            new() { ["phase"] = "starting", ["activityId"] = "RunWorker" });

        var result = ChatCompletionsEndpoints.FormatItem(item);

        Assert.NotNull(result);
        Assert.Contains("RunWorker", result);
        Assert.Contains("---", result);
    }

    [Fact]
    public void FormatItem_ActivityTransitionCompleted_ReturnsNull()
    {
        var item = MakeItem(ConversationItemKind.ActivityTransition, "done",
            new() { ["phase"] = "completed" });

        Assert.Null(ChatCompletionsEndpoints.FormatItem(item));
    }

    [Fact]
    public void FormatItem_ActivityTransitionFaulted_ReturnsNull()
    {
        var item = MakeItem(ConversationItemKind.ActivityTransition, "error",
            new() { ["phase"] = "faulted" });

        Assert.Null(ChatCompletionsEndpoints.FormatItem(item));
    }

    [Fact]
    public void FormatItem_ToolCallItem_ReturnsWrenchLine()
    {
        var item = MakeItem(ConversationItemKind.ToolCall, "Run(command=ls -la)",
            new() { ["agent"] = "team:Worker", ["tool"] = "Run" });

        var result = ChatCompletionsEndpoints.FormatItem(item);

        Assert.NotNull(result);
        Assert.Contains("🔧", result);
        Assert.Contains("team:Worker", result);
        Assert.Contains("Run(command=ls -la)", result);
    }

    [Fact]
    public void FormatItem_UserMessage_ReturnsNull()
    {
        // Le client l'a déjà affiché — on évite le doublon dans le flux content.
        var item = MakeItem(ConversationItemKind.UserMessage, "ma tâche");

        Assert.Null(ChatCompletionsEndpoints.FormatItem(item));
    }

    [Fact]
    public void FormatItem_NeedsHelpQuestion_ReturnsBadgeText()
    {
        var item = MakeItem(ConversationItemKind.NeedsHelpQuestion, "Quelle option choisir ?");

        var result = ChatCompletionsEndpoints.FormatItem(item);

        Assert.NotNull(result);
        Assert.Contains("Quelle option choisir ?", result);
    }

    // ── FormatLlmExchangeReasoningChunks ──────────────────────────────────────────────────────────

    [Fact]
    public void FormatLlmExchangeReasoningChunks_EmptyResponse_EmitsOnlyAgentHeader()
    {
        var response = new ChatResponse([]);
        var evt = new LlmExchangeStreamEvent("conv-1", "MyAgent", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        Assert.Single(chunks);
        Assert.Contains("MyAgent", chunks[0]);
    }

    [Fact]
    public void FormatLlmExchangeReasoningChunks_WithTextContent_EmitsTextChunk()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent("Réponse finale")])]);
        var evt = new LlmExchangeStreamEvent("conv-1", "Worker", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        Assert.Contains("Réponse finale", chunks);
    }

    [Fact]
    public void FormatLlmExchangeReasoningChunks_WithTextReasoningContent_EmitsThinkingChunk()
    {
        // Régression 1 : TextReasoningContent était silencieusement ignoré.
        var trc = new TextReasoningContent("Je réfléchis au problème...");
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [trc])]);
        var evt = new LlmExchangeStreamEvent("conv-1", "Worker", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        Assert.Contains("Je réfléchis au problème...", chunks);
    }

    [Fact]
    public void FormatLlmExchangeReasoningChunks_WithFunctionCallContent_EmitsNoExtraChunk()
    {
        // Régression 2 : FunctionCallContent ne doit PAS apparaître dans reasoning_content
        // (il est tracé séparément comme item ToolCall dans le flux content).
        var fc = new FunctionCallContent("call-1", "Run",
            new Dictionary<string, object?> { ["command"] = "ls -la" });
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [fc])]);
        var evt = new LlmExchangeStreamEvent("conv-1", "Worker", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        // Seul le header agent — aucun chunk pour le FunctionCall.
        Assert.Single(chunks);
        Assert.DoesNotContain(chunks, c => c.Contains("Run") || c.Contains("ls"));
    }

    [Fact]
    public void FormatLlmExchangeReasoningChunks_MixedContents_EmitsReasoningAndTextButNotFunctionCall()
    {
        // Vérifie l'ordre et la sélectivité sur un message mixte.
        var trc = new TextReasoningContent("Thinking...");
        var tc = new TextContent("Answer");
        var fc = new FunctionCallContent("call-1", "Run", null);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [trc, tc, fc])]);
        var evt = new LlmExchangeStreamEvent("conv-1", "Worker", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        // Header + thinking + answer = 3. FunctionCall = 0.
        Assert.Equal(3, chunks.Count);
        Assert.Contains("Thinking...", chunks);
        Assert.Contains("Answer", chunks);
    }

    [Fact]
    public void FormatLlmExchangeReasoningChunks_WhitespaceOnlyContent_IsSkipped()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent("   ")])]);
        var evt = new LlmExchangeStreamEvent("conv-1", "Worker", response);

        var chunks = ChatCompletionsEndpoints.FormatLlmExchangeReasoningChunks(evt).ToList();

        // Seulement le header — le texte vide/blanc est ignoré.
        Assert.Single(chunks);
    }
}
