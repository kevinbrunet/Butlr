using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

/// <summary>
/// Endpoint <c>POST /teams/{team}/v1/chat/completions</c> compatible OpenAI Chat Completions
/// (cf. SKILL openai-compat-api-spec) — expose le workflow Alveus comme un seul appel LLM.
/// Le client envoie la tâche dans <c>messages[-1].content</c> et reçoit en streaming SSE le
/// déroulement complet : <c>reasoning_content</c> pour les échanges LLM de chaque agent,
/// <c>content</c> pour les jalons du workflow (transitions d'activité, items conversation).
/// Requiert <c>"stream": true</c> ; le mode non-streaming retourne 501.
/// </summary>
public static class ChatCompletionsEndpoints
{
    private static readonly JsonSerializerOptions OaiOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record OaiChunk(string Id, string Object, long Created, string Model, OaiChoice[] Choices);
    private sealed record OaiChoice(int Index, OaiDelta Delta, string? FinishReason);
    private sealed record OaiDelta(string? Role = null, string? Content = null, string? ReasoningContent = null);

    // ── DTOs requête ─────────────────────────────────────────────────────────────────────────────

    private sealed record ChatRequest(
        string Model,
        ChatRequestMessage[] Messages,
        bool Stream = false);

    private sealed record ChatRequestMessage(string Role, string Content);

    // ── Registration ─────────────────────────────────────────────────────────────────────────────

    public static IEndpointRouteBuilder MapChatCompletionsEndpoints(
        this IEndpointRouteBuilder app, IEnumerable<string> teamNames)
    {
        foreach (var teamName in teamNames)
        {
            var tag = teamName;
            app.MapPost($"/teams/{teamName}/v1/chat/completions",
                (HttpContext ctx, IConversationStore store, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
                    HandleAsync(ctx, tag, store, scopeFactory, ct))
               .WithName($"ChatCompletions-{teamName}");
        }

        return app;
    }

    // ── Handler ──────────────────────────────────────────────────────────────────────────────────

    private static async Task HandleAsync(
        HttpContext httpContext,
        string teamName,
        IConversationStore store,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        ChatRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<ChatRequest>(OaiOptions, ct);
        }
        catch
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!request.Stream)
        {
            httpContext.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await httpContext.Response.WriteAsync(
                "{\"error\":{\"message\":\"Non-streaming mode not supported. Use stream:true.\"}}", ct);
            return;
        }

        var userMessage = request.Messages.LastOrDefault(m => m.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Conversation créée avant de démarrer le workflow pour que SubscribeEventsAsync
        // soit appelé avant RunInstanceAsync — aucun événement ne peut être perdu.
        var conversation = store.Create();
        var conversationId = conversation.Id;
        store.AddItem(conversationId, "user", userMessage, ConversationItemKind.UserMessage);

        // Abonnement IMMÉDIAT — le channel est ouvert avant que le workflow démarre.
        var events = store.SubscribeEventsAsync(conversationId, ct);

        // Workflow lancé en tâche de fond : RunInstanceAsync peut bloquer longtemps (appels LLM).
        var workflowTask = RunWorkflowAsync(store, scopeFactory, conversationId, userMessage, teamName, ct);

        // Début du flux SSE
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        var chatId = $"chatcmpl-{conversationId}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = string.IsNullOrWhiteSpace(request.Model) ? "alveus-workflow" : request.Model;

        await WriteChunkAsync(httpContext.Response, chatId, created, model,
            new OaiDelta(Role: "assistant"), null, ct);

        try
        {
            await foreach (var evt in events)
            {
                switch (evt)
                {
                    case ConversationItemStreamEvent { Item: var item }:
                        var text = FormatItem(item);
                        if (text is not null)
                            await WriteChunkAsync(httpContext.Response, chatId, created, model,
                                new OaiDelta(Content: text), null, ct);
                        break;

                    case LlmExchangeStreamEvent llm:
                        await WriteLlmChunksAsync(httpContext.Response, chatId, created, model, llm, ct);
                        break;

                    case WorkflowSuspendedStreamEvent:
                        // SSE est unidirectionnel : on ferme proprement le flux et le client
                        // doit reprendre via WebSocket (/ws) ou POST /conversations/{id}/items.
                        await WriteChunkAsync(httpContext.Response, chatId, created, model,
                            new OaiDelta(Content: "\n\n**[En attente de réponse — utilisez le canal WebSocket pour continuer]**\n"), null, ct);
                        await WriteDoneAsync(httpContext.Response, chatId, created, model, ct);
                        return;

                    case WorkflowCompletedStreamEvent completed:
                        var summary = completed.Status == "failed"
                            ? "\n\n**[Workflow terminé en erreur]**\n"
                            : "\n\n**[Workflow terminé]**\n";
                        await WriteChunkAsync(httpContext.Response, chatId, created, model,
                            new OaiDelta(Content: summary), null, ct);
                        await WriteDoneAsync(httpContext.Response, chatId, created, model, ct);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client SSE déconnecté. Le workflow continue en arrière-plan (scope indépendant).
        }

        await WriteDoneAsync(httpContext.Response, chatId, created, model, ct);

        // workflowTask couvre uniquement la création de l'instance Elsa (ms) — l'exécution
        // réelle tourne dans le Task.Run de RunWorkflowAsync avec son propre scope.
        await workflowTask;
    }

    private static async Task RunWorkflowAsync(
        IConversationStore store,
        IServiceScopeFactory scopeFactory,
        string conversationId,
        string taskPrompt,
        string teamName,
        CancellationToken ct)
    {
        // Créer l'instance dans le scope de la requête courante (opération rapide, pas de LLM).
        string workflowInstanceId;
        try
        {
            await using var requestScope = scopeFactory.CreateAsyncScope();
            var runtime = requestScope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
            var client = await runtime.CreateClientAsync(ct);
            await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(
                    ConversationEndpoints.WorkflowDefinitionId),
                CorrelationId = conversationId,
                Input = new Dictionary<string, object>
                {
                    ["TaskPrompt"] = taskPrompt,
                    ["ConversationId"] = conversationId,
                    ["TeamName"] = teamName,
                },
            }, ct);
            workflowInstanceId = client.WorkflowInstanceId;
        }
        catch (OperationCanceledException)
        {
            store.Complete(conversationId, failed: true);
            throw;
        }

        store.SetWorkflowInstanceId(conversationId, workflowInstanceId);

        // Lancer le workflow dans un scope DI indépendant : la requête HTTP peut se terminer
        // avant la fin du workflow (LLM lents, longues tâches). CancellationToken.None garantit
        // que le workflow ne sera pas annulé si le client SSE se déconnecte.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var bgScope = scopeFactory.CreateAsyncScope();
                var bgRuntime = bgScope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
                var bgClient = await bgRuntime.CreateClientAsync(workflowInstanceId, CancellationToken.None);
                var runResponse = await bgClient.RunInstanceAsync(new RunWorkflowInstanceRequest(), CancellationToken.None);

                switch (runResponse.SubStatus)
                {
                    case Elsa.Workflows.WorkflowSubStatus.Finished:
                        store.Complete(conversationId, failed: false);
                        break;
                    case Elsa.Workflows.WorkflowSubStatus.Faulted:
                    case Elsa.Workflows.WorkflowSubStatus.Cancelled:
                        store.AddItem(conversationId, "assistant",
                            "Le workflow a échoué — voir les logs du serveur pour le détail.",
                            ConversationItemKind.ActivityTransition,
                            new Dictionary<string, string> { ["phase"] = "error" });
                        store.Complete(conversationId, failed: true);
                        break;
                    case Elsa.Workflows.WorkflowSubStatus.Suspended:
                        store.PublishSuspended(conversationId);
                        break;
                }
            }
            catch (Exception ex)
            {
                store.AddItem(conversationId, "assistant",
                    $"Erreur inattendue : {ex.Message}",
                    ConversationItemKind.ActivityTransition,
                    new Dictionary<string, string> { ["phase"] = "error" });
                store.Complete(conversationId, failed: true);
            }
        });
    }

    // ── Formatage SSE ─────────────────────────────────────────────────────────────────────────────

    private static async Task WriteLlmChunksAsync(
        HttpResponse response, string chatId, long created, string model,
        LlmExchangeStreamEvent llm, CancellationToken ct)
    {
        foreach (var chunk in FormatLlmExchangeReasoningChunks(llm))
        {
            await WriteChunkAsync(response, chatId, created, model,
                new OaiDelta(ReasoningContent: chunk), null, ct);
        }
    }

    /// <summary>
    /// Convertit un <see cref="LlmExchangeStreamEvent"/> en séquence de fragments
    /// <c>reasoning_content</c> SSE. <see cref="FunctionCallContent"/> est omis
    /// volontairement — il est tracé via l'item <see cref="ConversationItemKind.ToolCall"/>.
    /// </summary>
    internal static IEnumerable<string> FormatLlmExchangeReasoningChunks(LlmExchangeStreamEvent llm)
    {
        yield return $"\n\n**[{llm.AgentName}]**\n";
        foreach (var message in llm.Response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent trc when !string.IsNullOrWhiteSpace(trc.Text):
                        yield return trc.Text;
                        break;
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        yield return tc.Text;
                        break;
                    // FunctionCallContent omis : tracé comme ToolCall item dans le flux Content.
                }
            }
        }
    }

    internal static string? FormatItem(ConversationItem item) => item.Kind switch
    {
        ConversationItemKind.ActivityTransition
            when item.Metadata.GetValueOrDefault("phase") == "starting"
            => $"\n\n---\n**⚙ {item.Metadata.GetValueOrDefault("activityId", "?")}**\n",
        ConversationItemKind.ActivityTransition => null,
        ConversationItemKind.UserMessage => null, // déjà envoyé par le client
        ConversationItemKind.HumanReply => $"\n\n**[Human]** {item.Text}\n",
        ConversationItemKind.NeedsHelpQuestion => $"\n\n**❓** {item.Text}\n",
        ConversationItemKind.FileEdit
            => $"\n📝 `{item.Metadata.GetValueOrDefault("command", "?")}` → `{item.Metadata.GetValueOrDefault("path", "?")}`\n",
        ConversationItemKind.MeetingRound
            => $"\n\n**Round {item.Metadata.GetValueOrDefault("round", "?")}:**\n{item.Text}\n",
        ConversationItemKind.ExpertQuestion
            => $"\n\n**❓ Expert [{item.Metadata.GetValueOrDefault("expert", "?")}]:** {item.Text}\n",
        ConversationItemKind.ExpertAnswer
            => $"\n\n**💬 Expert [{item.Metadata.GetValueOrDefault("expert", "?")}]:** {item.Text}\n",
        ConversationItemKind.AssistantMessage => $"\n\n**[Résultat]** {item.Text}\n",
        ConversationItemKind.ToolCall
            => $"\n🔧 `{item.Metadata.GetValueOrDefault("agent", "?")}` → `{item.Text}`\n",
        _ => null,
    };

    private static async Task WriteChunkAsync(
        HttpResponse response, string id, long created, string model,
        OaiDelta delta, string? finishReason, CancellationToken ct)
    {
        var chunk = new OaiChunk(id, "chat.completion.chunk", created, model,
            [new OaiChoice(0, delta, finishReason)]);
        var json = JsonSerializer.Serialize(chunk, OaiOptions);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteDoneAsync(
        HttpResponse response, string id, long created, string model, CancellationToken ct)
    {
        await WriteChunkAsync(response, id, created, model, new OaiDelta(), "stop", ct);
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
