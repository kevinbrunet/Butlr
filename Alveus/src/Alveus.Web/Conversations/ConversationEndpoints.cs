using System.Text.Json;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;

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
                (CreateConversationRequest req, IConversationStore store, IWorkflowRuntime runtime, CancellationToken ct) =>
                    CreateConversationAsync(req, tag, store, runtime, ct))
                .WithName($"CreateConversation-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}", GetConversation).WithName($"GetConversation-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}/items", ListConversationItems).WithName($"ListConversationItems-{teamName}");
            app.MapPost($"{prefix}/v1/conversations/{{id}}/items", AddConversationItemsAsync).WithName($"AddConversationItems-{teamName}");
            app.MapGet($"{prefix}/v1/conversations/{{id}}/stream", StreamConversationAsync).WithName($"StreamConversation-{teamName}");
        }

        return app;
    }

    internal static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest request,
        string teamName,
        IConversationStore store,
        IWorkflowRuntime runtime,
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

        store.SetWorkflowInstanceId(conversation.Id, client.WorkflowInstanceId);

        var runResponse = await client.RunInstanceAsync(new RunWorkflowInstanceRequest(), cancellationToken);
        ApplyRunResult(store, conversation.Id, runResponse);

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
}
