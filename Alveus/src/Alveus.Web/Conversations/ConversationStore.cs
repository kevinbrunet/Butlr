using System.Collections.Concurrent;
using System.Threading.Channels;
using Alveus.Web.Logging;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Conversations;

/// <inheritdoc cref="IConversationStore"/>
public sealed class ConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly ITaskLogger? _logger;

    // ITaskLogger est nullable pour faciliter les tests unitaires (pas de DI).
    public ConversationStore(ITaskLogger? logger = null) => _logger = logger;

    public ConversationState Create()
    {
        var state = new ConversationState { Id = Guid.NewGuid().ToString("N") };
        _conversations[state.Id] = state;
        return state;
    }

    public ConversationState? Get(string conversationId)
        => _conversations.GetValueOrDefault(conversationId);

    public ConversationItem AddItem(
        string conversationId,
        string role,
        string text,
        ConversationItemKind kind,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var state = GetRequired(conversationId);
        var item = new ConversationItem(
            Guid.NewGuid().ToString("N"),
            conversationId,
            role,
            text,
            kind,
            metadata ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        lock (state.Lock)
        {
            state.Items.Add(item);
        }

        foreach (var channel in state.Subscribers.Values)
        {
            channel.Writer.TryWrite(item);
        }

        var evt = new ConversationItemStreamEvent(item);
        foreach (var channel in state.EventSubscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }

        _logger?.OnItem(item);
        return item;
    }

    public IReadOnlyList<ConversationItem> GetItems(string conversationId, string? after = null, int? limit = null)
    {
        var state = GetRequired(conversationId);

        lock (state.Lock)
        {
            IEnumerable<ConversationItem> items = state.Items;

            if (after is not null)
            {
                var afterIndex = state.Items.FindIndex(i => i.Id == after);
                items = afterIndex >= 0 ? state.Items.Skip(afterIndex + 1) : items;
            }

            if (limit is not null)
            {
                items = items.Take(limit.Value);
            }

            return items.ToList();
        }
    }

    public void SetWorkflowInstanceId(string conversationId, string workflowInstanceId)
    {
        var state = GetRequired(conversationId);
        lock (state.Lock)
        {
            state.WorkflowInstanceId = workflowInstanceId;
        }
    }

    public void SetPendingBookmark(string conversationId, string bookmarkId)
    {
        var state = GetRequired(conversationId);
        lock (state.Lock)
        {
            state.PendingBookmarkId = bookmarkId;
            state.Status = "awaiting_input";
        }
    }

    public (string WorkflowInstanceId, string BookmarkId)? TryResolvePendingBookmark(string conversationId)
    {
        var state = GetRequired(conversationId);
        lock (state.Lock)
        {
            if (state.Status != "awaiting_input" || state.PendingBookmarkId is null || state.WorkflowInstanceId is null)
            {
                return null;
            }

            var result = (state.WorkflowInstanceId, state.PendingBookmarkId);
            state.PendingBookmarkId = null;
            state.Status = "running";
            return result;
        }
    }

    public void Complete(string conversationId, bool failed = false)
    {
        var status = failed ? "failed" : "completed";
        var state = GetRequired(conversationId);
        lock (state.Lock)
        {
            state.Status = status;
        }

        foreach (var channel in state.Subscribers.Values)
        {
            channel.Writer.TryComplete();
        }

        var completedEvt = new WorkflowCompletedStreamEvent(conversationId, status);
        foreach (var channel in state.EventSubscribers.Values)
        {
            channel.Writer.TryWrite(completedEvt);
            channel.Writer.TryComplete();
        }

        _logger?.OnCompleted(conversationId, status);
    }

    public async IAsyncEnumerable<ConversationItem> SubscribeAsync(
        string conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = GetRequired(conversationId);
        var channel = Channel.CreateUnbounded<ConversationItem>();
        var subscriberId = Guid.NewGuid();
        state.Subscribers[subscriberId] = channel;

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            state.Subscribers.TryRemove(subscriberId, out _);
        }
    }

    public void PublishLlmExchange(string conversationId, string agentName, ChatResponse response)
    {
        if (!_conversations.TryGetValue(conversationId, out var state)) return;
        var evt = new LlmExchangeStreamEvent(conversationId, agentName, response);
        foreach (var channel in state.EventSubscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }

    public void PublishSuspended(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var state)) return;
        var evt = new WorkflowSuspendedStreamEvent(conversationId);
        foreach (var channel in state.EventSubscribers.Values)
        {
            // Channel intentionnellement laissé ouvert : les abonnés continuent à écouter
            // pour recevoir les événements de la reprise après réponse humaine.
            channel.Writer.TryWrite(evt);
        }
    }

    public IAsyncEnumerable<WorkflowStreamEvent> SubscribeEventsAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var state = GetRequired(conversationId);
        var channel = Channel.CreateUnbounded<WorkflowStreamEvent>();
        var subscriberId = Guid.NewGuid();
        // Abonnement IMMÉDIAT (avant que l'appelant commence à consommer) : les événements produits
        // entre cet appel et le premier MoveNextAsync sont mis en tampon dans le channel, sans perte.
        state.EventSubscribers[subscriberId] = channel;
        return DrainChannelAsync(channel, subscriberId, state, cancellationToken);
    }

    private static async IAsyncEnumerable<WorkflowStreamEvent> DrainChannelAsync(
        Channel<WorkflowStreamEvent> channel,
        Guid subscriberId,
        ConversationState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            state.EventSubscribers.TryRemove(subscriberId, out _);
        }
    }

    private ConversationState GetRequired(string conversationId)
        => _conversations.TryGetValue(conversationId, out var state)
            ? state
            : throw new KeyNotFoundException($"Conversation introuvable : '{conversationId}'.");
}
