using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

/// <summary>
/// Endpoint WebSocket <c>ws://…/teams/{team}/ws</c> compatible OpenAI Responses API WebSocket mode.
/// Le client envoie <c>response.create</c> avec la tâche ; le serveur streame le déroulement du
/// workflow. Si le workflow se suspend (<see cref="Activities.AwaitConversationReply"/>), le serveur
/// envoie <c>response.completed(status=incomplete)</c> et attend un nouveau <c>response.create</c>
/// avec <c>previous_response_id</c> sur la MÊME connexion WebSocket — sans rouvrir de canal.
/// </summary>
public static class WebSocketEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── DTOs client → serveur ────────────────────────────────────────────────────────────────────

    private sealed record ClientEvent(
        string Type,
        string? PreviousResponseId = null,
        ClientInputItem[]? Input = null);

    private sealed record ClientInputItem(string Role, string Content);

    // ── DTOs serveur → client ─────────────────────────────────────────────────────────────────────
    // Champs conformes au spec openai-openapi.yaml (ResponsesServerEvent / ResponseStreamEvent).
    // Champ `sequence_number` : required sur tous les events server → client.
    // `logprobs` : required sur ResponseTextDeltaEvent — envoyé comme tableau vide (on n'a pas de logprobs).

    private sealed record ResponseObject(string Id, string Object, long CreatedAt, string Status,
        object? Error = null, object? IncompleteDetails = null, string? Model = null);

    private sealed record ResponseCreatedEvent(string Type, ResponseObject Response, int SequenceNumber);
    private sealed record ResponseCompletedEvent(string Type, ResponseObject Response, int SequenceNumber);
    private sealed record ResponseIncompleteEvent(string Type, ResponseObject Response, int SequenceNumber);
    private sealed record ResponseFailedEvent(string Type, ResponseObject Response, int SequenceNumber);

    private sealed record OutputTextDeltaEvent(string Type, string ItemId, int OutputIndex,
        int ContentIndex, string Delta, int SequenceNumber, object[] Logprobs);

    private sealed record ReasoningDeltaEvent(string Type, string ItemId, int OutputIndex,
        int SummaryIndex, string Delta, int SequenceNumber);

    private sealed record ErrorEvent(string Type, string? Code, string Message, string? Param, int SequenceNumber);

    private static readonly long _epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static ResponseCreatedEvent Created(string id, int seq) =>
        new("response.created", new ResponseObject(id, "response", _epoch, "in_progress"), seq);

    private static OutputTextDeltaEvent TextDelta(string itemId, string delta, int seq) =>
        new("response.output_text.delta", itemId, 0, 0, delta, seq, []);

    private static ReasoningDeltaEvent ThinkingDelta(string itemId, string delta, int seq) =>
        new("response.reasoning_summary_text.delta", itemId, 0, 0, delta, seq);

    private static ResponseCompletedEvent Completed(string id, int seq) =>
        new("response.completed", new ResponseObject(id, "response", _epoch, "completed"), seq);

    private static ResponseIncompleteEvent Incomplete(string id, int seq) =>
        new("response.incomplete",
            new ResponseObject(id, "response", _epoch, "incomplete",
                IncompleteDetails: new { reason = "awaiting_input" }),
            seq);

    private static ResponseFailedEvent Failed(string id, int seq) =>
        new("response.failed", new ResponseObject(id, "response", _epoch, "failed"), seq);

    private static ErrorEvent Error(string msg, int seq) =>
        new("error", "server_error", msg, null, seq);

    // ── Registration ─────────────────────────────────────────────────────────────────────────────

    public static IEndpointRouteBuilder MapWebSocketEndpoints(
        this IEndpointRouteBuilder app, IEnumerable<string> teamNames)
    {
        foreach (var teamName in teamNames)
        {
            var tag = teamName;
            app.Map($"/teams/{teamName}/ws", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var store = ctx.RequestServices.GetRequiredService<IConversationStore>();
                var runtime = ctx.RequestServices.GetRequiredService<IWorkflowRuntime>();
                await RunSessionAsync(ws, tag, store, runtime, ctx.RequestAborted);
            });
        }
        return app;
    }

    // ── Session ───────────────────────────────────────────────────────────────────────────────────

    private static async Task RunSessionAsync(
        WebSocket ws, string teamName,
        IConversationStore store, IWorkflowRuntime runtime,
        CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveJsonAsync<ClientEvent>(ws, ct);
                if (msg is null || msg.Type != "response.create") break;

                string conversationId;
                // sequence_number démarre à 1 pour chaque segment de réponse (response.create → response.completed/incomplete).
                var seq = 1;

                if (msg.PreviousResponseId is null)
                {
                    // ── Nouvelle conversation ──────────────────────────────────────────────────
                    var taskPrompt = ExtractUserContent(msg.Input);
                    if (taskPrompt is null) { await SendAsync(ws, Error("Un item 'user' est requis.", seq++), ct); continue; }

                    var conv = store.Create();
                    conversationId = conv.Id;
                    store.AddItem(conversationId, "user", taskPrompt, ConversationItemKind.UserMessage);

                    // Subscribe AVANT de démarrer le workflow pour éviter toute perte d'événement.
                    var events = store.SubscribeEventsAsync(conversationId, ct);
                    _ = RunWorkflowSegmentAsync(store, runtime, conversationId, taskPrompt, teamName,
                        bookmarkId: null, input: null, ct);

                    await SendAsync(ws, Created(conversationId, seq++), ct);
                    var suspended = await StreamAsync(ws, conversationId, events, seq, ct);

                    if (!suspended) continue; // completed ou déconnexion — la boucle vérifie ws.State
                    // Suspendu → on boucle pour attendre le prochain response.create
                }
                else
                {
                    // ── Reprise après suspension ───────────────────────────────────────────────
                    conversationId = msg.PreviousResponseId;
                    var reply = ExtractUserContent(msg.Input);
                    if (reply is null) { await SendAsync(ws, Error("Un item 'user' est requis pour reprendre.", seq++), ct); continue; }

                    var pending = store.TryResolvePendingBookmark(conversationId);
                    if (pending is null) { await SendAsync(ws, Error($"Aucun bookmark en attente pour '{conversationId}'.", seq++), ct); continue; }

                    // Subscribe AVANT de démarrer la reprise.
                    var resumeEvents = store.SubscribeEventsAsync(conversationId, ct);
                    _ = RunWorkflowSegmentAsync(store, runtime, conversationId, reply, teamName,
                        bookmarkId: pending.Value.BookmarkId,
                        input: new Dictionary<string, object>
                        {
                            ["Reply"] = reply,
                            // WorkflowInstanceId fourni par TryResolvePendingBookmark
                            ["__WorkflowInstanceId__"] = pending.Value.WorkflowInstanceId,
                        },
                        ct);

                    await SendAsync(ws, Created(conversationId, seq++), ct);
                    var suspended = await StreamAsync(ws, conversationId, resumeEvents, seq, ct);
                    if (!suspended) continue;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>
    /// Lit les événements et les pousse au WebSocket jusqu'à complétion ou suspension.
    /// Retourne <c>true</c> si le workflow est suspendu (attente réponse humaine),
    /// <c>false</c> si complété ou connexion fermée.
    /// <paramref name="seq"/> est la valeur initiale du sequence_number (incrémenté à chaque event).
    /// </summary>
    private static async Task<bool> StreamAsync(
        WebSocket ws, string conversationId,
        IAsyncEnumerable<WorkflowStreamEvent> events,
        int seq,
        CancellationToken ct)
    {
        await foreach (var evt in events)
        {
            switch (evt)
            {
                case ConversationItemStreamEvent { Item: var item }:
                    var text = FormatItem(item);
                    if (text is not null)
                        await SendAsync(ws, TextDelta(item.Id, text, seq++), ct);
                    break;

                case LlmExchangeStreamEvent llm:
                    seq = await StreamLlmAsync(ws, conversationId, llm, seq, ct);
                    break;

                case WorkflowSuspendedStreamEvent:
                    await SendAsync(ws, Incomplete(conversationId, seq), ct);
                    return true; // Suspendu — le canal WebSocket reste ouvert

                case WorkflowCompletedStreamEvent { Status: var status }:
                    if (status == "failed")
                        await SendAsync(ws, Failed(conversationId, seq), ct);
                    else
                        await SendAsync(ws, Completed(conversationId, seq), ct);
                    return false;
            }
        }
        await SendAsync(ws, Completed(conversationId, seq), ct);
        return false;
    }

    // ── Workflow ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exécute un segment de workflow (initial ou reprise) en arrière-plan et publie
    /// l'événement de complétion ou suspension dans le channel de la conversation.
    /// </summary>
    private static async Task RunWorkflowSegmentAsync(
        IConversationStore store,
        IWorkflowRuntime runtime,
        string conversationId,
        string taskOrReply,
        string teamName,
        string? bookmarkId,
        Dictionary<string, object>? input,
        CancellationToken ct)
    {
        try
        {
            IWorkflowClient client;

            if (bookmarkId is not null && input is not null
                && input.TryGetValue("__WorkflowInstanceId__", out var wfIdObj)
                && wfIdObj is string wfId)
            {
                // Reprise d'une instance existante
                input.Remove("__WorkflowInstanceId__");
                client = await runtime.CreateClientAsync(wfId, ct);
            }
            else
            {
                // Nouvelle instance
                client = await runtime.CreateClientAsync(ct);
                await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
                {
                    WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(
                        ConversationEndpoints.WorkflowDefinitionId),
                    CorrelationId = conversationId,
                    Input = new Dictionary<string, object>
                    {
                        ["TaskPrompt"] = taskOrReply,
                        ["ConversationId"] = conversationId,
                        ["TeamName"] = teamName,
                    },
                }, ct);
                store.SetWorkflowInstanceId(conversationId, client.WorkflowInstanceId);
                bookmarkId = null;
                input = null;
            }

            var req = bookmarkId is not null
                ? new RunWorkflowInstanceRequest { BookmarkId = bookmarkId, Input = input }
                : new RunWorkflowInstanceRequest();

            var result = await client.RunInstanceAsync(req, ct);

            switch (result.SubStatus)
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
                // Pending/Executing : le workflow tourne en arrière-plan Elsa, rien à faire ici.
            }
        }
        catch (OperationCanceledException ex)
        {
            store.AddItem(conversationId, "assistant",
                $"Le workflow a été annulé : {ex.Message}",
                ConversationItemKind.ActivityTransition,
                new Dictionary<string, string> { ["phase"] = "error" });
            store.Complete(conversationId, failed: true);
        }
        catch (Exception ex)
        {
            store.AddItem(conversationId, "assistant",
                $"Erreur inattendue : {ex.Message}",
                ConversationItemKind.ActivityTransition,
                new Dictionary<string, string> { ["phase"] = "error" });
            store.Complete(conversationId, failed: true);
        }
    }

    // ── Formatage ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> StreamLlmAsync(
        WebSocket ws, string conversationId, LlmExchangeStreamEvent llm, int seq, CancellationToken ct)
    {
        await SendAsync(ws, ThinkingDelta(conversationId, $"\n\n**[{llm.AgentName}]**\n", seq++), ct);

        foreach (var message in llm.Response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        await SendAsync(ws, ThinkingDelta(conversationId, tc.Text, seq++), ct);
                        break;
                    case FunctionCallContent fc:
                        var args = fc.Arguments is not null
                            ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : string.Empty;
                        await SendAsync(ws, ThinkingDelta(conversationId, $"\n⟨{fc.Name}({args})⟩", seq++), ct);
                        break;
                }
            }
        }

        return seq;
    }

    private static string? FormatItem(ConversationItem item) => item.Kind switch
    {
        ConversationItemKind.ActivityTransition
            when item.Metadata.GetValueOrDefault("phase") == "starting"
            => $"\n\n---\n**⚙ {item.Metadata.GetValueOrDefault("activityId", "?")}**\n",
        ConversationItemKind.ActivityTransition => null,
        ConversationItemKind.UserMessage => null,
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

    private static string? ExtractUserContent(ClientInputItem[]? items) =>
        items?.FirstOrDefault(m => m.Role == "user")?.Content is { Length: > 0 } c ? c : null;

    // ── Helpers WebSocket ─────────────────────────────────────────────────────────────────────────

    private static async Task SendAsync<T>(WebSocket ws, T payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<T?> ReceiveJsonAsync<T>(WebSocket ws, CancellationToken ct)
        where T : class
    {
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (ws.State == WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Seek(0, System.IO.SeekOrigin.Begin);
        try { return await JsonSerializer.DeserializeAsync<T>(ms, JsonOpts, ct); }
        catch { return null; }
    }
}
