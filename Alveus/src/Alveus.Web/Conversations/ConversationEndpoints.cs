using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

/// <summary>
/// API HTTP self-hosted au format OpenAI Conversations (sous-ensemble, cf. ADR 0027) — point
/// d'entrée et canal d'aide humaine/observabilité pour <c>AlveusTaskWorkflow</c>. Démarre/reprend le
/// workflow via <see cref="IWorkflowRuntime.CreateClientAsync(System.Threading.CancellationToken)"/>
/// (<see cref="IWorkflowClient.CreateInstanceAsync"/>/<see cref="IWorkflowClient.RunInstanceAsync"/>),
/// jamais <c>IWorkflowRuntime.StartWorkflowAsync</c>/<c>ResumeWorkflowAsync</c> (obsolètes en Elsa
/// 3.7.0). La reprise après "NeedsHelp" passe par
/// <see cref="IConversationStore.TryResolvePendingBookmark"/>, posé par
/// <see cref="Activities.AwaitConversationReply"/>.
/// </summary>
public static class ConversationEndpoints
{
    internal const string WorkflowDefinitionId = "AlveusTaskWorkflow";

    /// <summary>
    /// Enregistre les routes de conversation pour chaque équipe déclarée (cf. ADR 0031).
    /// Chaque équipe obtient un préfixe <c>/teams/{teamName}/v1/conversations</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app, IEnumerable<string> teamNames)
    {
        foreach (var teamName in teamNames)
        {
            var prefix = $"/teams/{teamName}";
            var tag = teamName;
            app.MapPost($"{prefix}/v1/conversations",
                (CreateConversationRequest req, IConversationStore store, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
                    CreateConversationAsync(req, tag, store, scopeFactory, ct))
                .WithName($"CreateConversation-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}", GetConversation).WithName($"GetConversation-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}/items", ListConversationItems).WithName($"ListConversationItems-{teamName}");
            app.MapPost($"{prefix}/v1/conversations/{{id}}/items", AddConversationItemsAsync).WithName($"AddConversationItems-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}/stream", StreamConversationAsync).WithName($"StreamConversation-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}/oai-stream", StreamOaiAsync).WithName($"StreamOai-{teamName}");
        }

        return app;
    }

    internal static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest request,
        string teamName,
        IConversationStore store,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var firstItem = request.Items.FirstOrDefault();
        if (firstItem is null || firstItem.Role != "user")
        {
            return Results.BadRequest("Le premier item doit être un message 'user' (TaskPrompt).");
        }

        var taskPrompt = ExtractText(firstItem);
        var conversation = store.Create();
        store.AddItem(conversation.Id, "user", taskPrompt, ConversationItemKind.UserMessage);

        // Créer l'instance dans le scope de la requête courante.
        await using var requestScope = scopeFactory.CreateAsyncScope();
        var runtime = requestScope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(cancellationToken);
        await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(WorkflowDefinitionId),
            CorrelationId = conversation.Id,
            Input = new Dictionary<string, object>
            {
                ["TaskPrompt"] = taskPrompt,
                ["ConversationId"] = conversation.Id,
                ["TeamName"] = teamName,
            },
        }, cancellationToken);

        var workflowInstanceId = client.WorkflowInstanceId;
        store.SetWorkflowInstanceId(conversation.Id, workflowInstanceId);

        // Lancer le workflow dans un scope DI indépendant : IWorkflowRuntime est scoped et serait
        // disposé à la fin de la requête HTTP si on le capturait dans la closure.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var bgScope = scopeFactory.CreateAsyncScope();
                var bgRuntime = bgScope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
                var bgClient = await bgRuntime.CreateClientAsync(workflowInstanceId, CancellationToken.None);
                var runResponse = await bgClient.RunInstanceAsync(new RunWorkflowInstanceRequest(), CancellationToken.None);
                ApplyRunResult(store, conversation.Id, runResponse);
            }
            catch (Exception ex)
            {
                store.AddItem(conversation.Id, "assistant",
                    $"Erreur inattendue : {ex.Message}",
                    ConversationItemKind.ActivityTransition,
                    new Dictionary<string, string> { ["phase"] = "error" });
                store.Complete(conversation.Id, failed: true);
            }
        });

        return Results.Ok(ToConversationResponse(store.Get(conversation.Id)!));
    }

    internal static IResult GetConversation(string id, IConversationStore store)
    {
        var conversation = store.Get(id);
        return conversation is null ? Results.NotFound() : Results.Ok(ToConversationResponse(conversation));
    }

    internal static IResult ListConversationItems(string id, string? after, int? limit, IConversationStore store)
    {
        if (store.Get(id) is null)
        {
            return Results.NotFound();
        }

        var data = store.GetItems(id, after, limit).Select(ToItemResponse).ToList();
        return Results.Ok(new ConversationItemListResponse("list", data, false, data.FirstOrDefault()?.Id, data.LastOrDefault()?.Id));
    }

    internal static async Task<IResult> AddConversationItemsAsync(
        string id,
        AddConversationItemsRequest request,
        IConversationStore store,
        IWorkflowRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (store.Get(id) is null)
        {
            return Results.NotFound();
        }

        var firstItem = request.Items.FirstOrDefault();
        if (firstItem is null)
        {
            return Results.BadRequest("Au moins un item est requis.");
        }

        var text = ExtractText(firstItem);

        var pending = store.TryResolvePendingBookmark(id);
        if (pending is not null)
        {
            var resumeClient = await runtime.CreateClientAsync(pending.Value.WorkflowInstanceId, cancellationToken);
            var resumeResponse = await resumeClient.RunInstanceAsync(new RunWorkflowInstanceRequest
            {
                BookmarkId = pending.Value.BookmarkId,
                Input = new Dictionary<string, object> { ["Reply"] = text },
            }, cancellationToken);
            ApplyRunResult(store, id, resumeResponse);
        }
        else
        {
            store.AddItem(id, "user", text, ConversationItemKind.UserMessage);
        }

        return Results.Accepted();
    }

    private static async Task StreamConversationAsync(string id, HttpContext httpContext, IConversationStore store, CancellationToken cancellationToken)
    {
        if (store.Get(id) is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";

        foreach (var item in store.GetItems(id))
        {
            await WriteItemAsync(httpContext.Response, item, cancellationToken);
        }

        await foreach (var item in store.SubscribeAsync(id, cancellationToken))
        {
            await WriteItemAsync(httpContext.Response, item, cancellationToken);
        }
    }

    private static async Task WriteItemAsync(HttpResponse response, ConversationItem item, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(ToItemResponse(item));
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static void ApplyRunResult(IConversationStore store, string conversationId, RunWorkflowInstanceResponse response)
    {
        switch (response.SubStatus)
        {
            case WorkflowSubStatus.Finished:
                store.Complete(conversationId, failed: false);
                break;

            case WorkflowSubStatus.Faulted:
            case WorkflowSubStatus.Cancelled:
                store.AddItem(conversationId, "assistant",
                    "Le workflow a été interrompu de manière inattendue.",
                    ConversationItemKind.ActivityTransition,
                    new Dictionary<string, string> { ["phase"] = "error" });
                store.Complete(conversationId, failed: true);
                break;

            default:
                // Pending/Executing : en cours. Suspended : déjà passé à "awaiting_input" par
                // AwaitConversationReply via IConversationStore.SetPendingBookmark.
                break;
        }
    }

    private static string ExtractText(ConversationItemRequest item)
        => string.Join("\n", item.Content.Where(c => c.Type is "input_text" or "output_text" or "text").Select(c => c.Text));

    private static ConversationResponse ToConversationResponse(ConversationState conversation)
        => new(conversation.Id, "conversation", conversation.CreatedAt.ToUnixTimeSeconds(), conversation.Status, new Dictionary<string, object>());

    private static ConversationItemResponse ToItemResponse(ConversationItem item)
    {
        var metadata = new Dictionary<string, string>(item.Metadata) { ["kind"] = ToKind(item.Kind) };
        return new ConversationItemResponse(
            item.Id,
            "conversation.item",
            "message",
            item.Role,
            [new ConversationContentPart("output_text", item.Text)],
            item.CreatedAt.ToUnixTimeSeconds(),
            metadata);
    }

    private static string ToKind(ConversationItemKind kind) => kind switch
    {
        ConversationItemKind.UserMessage => "user_message",
        ConversationItemKind.AssistantMessage => "assistant_message",
        ConversationItemKind.ActivityTransition => "activity_transition",
        ConversationItemKind.FileEdit => "file_edit",
        ConversationItemKind.MeetingRound => "meeting_round",
        ConversationItemKind.NeedsHelpQuestion => "needs_help_question",
        ConversationItemKind.HumanReply => "human_reply",
        ConversationItemKind.ExpertQuestion => "expert_question",
        ConversationItemKind.ExpertAnswer => "expert_answer",
        _ => "unknown",
    };

    // ── OAI stream ──────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions OaiOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record OaiChunk(string Id, string Object, long Created, string Model, OaiChoice[] Choices);
    private sealed record OaiChoice(int Index, OaiDelta Delta, string? FinishReason);
    private sealed record OaiDelta(string? Role = null, string? Content = null, string? ReasoningContent = null);

    private static async Task StreamOaiAsync(string id, HttpContext httpContext, IConversationStore store, CancellationToken ct)
    {
        var conversation = store.Get(id);
        if (conversation is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        var chatId = $"chatcmpl-{id}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await WriteOaiChunkAsync(httpContext.Response, chatId, created, new OaiDelta(Role: "assistant"), null, ct);

        foreach (var item in store.GetItems(id))
        {
            var text = FormatItemForOai(item);
            if (text is not null)
                await WriteOaiChunkAsync(httpContext.Response, chatId, created, new OaiDelta(Content: text), null, ct);
        }

        if (conversation.Status is "completed" or "failed")
        {
            await WriteOaiDoneAsync(httpContext.Response, chatId, created, ct);
            return;
        }

        await foreach (var evt in store.SubscribeEventsAsync(id, ct))
        {
            switch (evt)
            {
                case ConversationItemStreamEvent { Item: var item }:
                    var text = FormatItemForOai(item);
                    if (text is not null)
                        await WriteOaiChunkAsync(httpContext.Response, chatId, created, new OaiDelta(Content: text), null, ct);
                    break;

                case LlmExchangeStreamEvent llm:
                    await WriteLlmExchangeChunksAsync(httpContext.Response, chatId, created, llm, ct);
                    break;

                case WorkflowCompletedStreamEvent:
                    await WriteOaiDoneAsync(httpContext.Response, chatId, created, ct);
                    return;
            }
        }

        await WriteOaiDoneAsync(httpContext.Response, chatId, created, ct);
    }

    private static async Task WriteLlmExchangeChunksAsync(HttpResponse response, string chatId, long created, LlmExchangeStreamEvent llm, CancellationToken ct)
    {
        await WriteOaiChunkAsync(response, chatId, created,
            new OaiDelta(ReasoningContent: $"\n\n**[{llm.AgentName}]**\n"), null, ct);

        foreach (var message in llm.Response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        await WriteOaiChunkAsync(response, chatId, created,
                            new OaiDelta(ReasoningContent: tc.Text), null, ct);
                        break;

                    case FunctionCallContent fc:
                        var args = fc.Arguments is not null
                            ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : string.Empty;
                        await WriteOaiChunkAsync(response, chatId, created,
                            new OaiDelta(ReasoningContent: $"\n⟨{fc.Name}({args})⟩"), null, ct);
                        break;
                }
            }
        }
    }

    private static string? FormatItemForOai(ConversationItem item) => item.Kind switch
    {
        ConversationItemKind.ActivityTransition
            when item.Metadata.GetValueOrDefault("phase") == "starting"
            => $"\n\n---\n**⚙ {item.Metadata.GetValueOrDefault("activityId", "?")}**\n",
        ConversationItemKind.ActivityTransition
            when item.Metadata.GetValueOrDefault("phase") == "outcome"
            => $"\n**→ {item.Text}**\n",
        ConversationItemKind.ActivityTransition
            when item.Metadata.GetValueOrDefault("phase") == "error"
            => $"\n\n⚠ {item.Text}\n",
        ConversationItemKind.ActivityTransition => null,
        ConversationItemKind.UserMessage => $"\n\n**[User]** {item.Text}\n",
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
        ConversationItemKind.AssistantMessage => $"\n\n**[Assistant]** {item.Text}\n",
        _ => null,
    };

    private static async Task WriteOaiChunkAsync(HttpResponse response, string id, long created, OaiDelta delta, string? finishReason, CancellationToken ct)
    {
        var chunk = new OaiChunk(id, "chat.completion.chunk", created, "alveus-workflow",
            [new OaiChoice(0, delta, finishReason)]);
        var json = JsonSerializer.Serialize(chunk, OaiOptions);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteOaiDoneAsync(HttpResponse response, string id, long created, CancellationToken ct)
    {
        await WriteOaiChunkAsync(response, id, created, new OaiDelta(), "stop", ct);
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
