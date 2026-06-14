using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Alveus.Web.Conversations;

/// <inheritdoc cref="IConversationStore"/>
public sealed class ConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();

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
        var state = GetRequired(conversationId);
        lock (state.Lock)
        {
            state.Status = failed ? "failed" : "completed";
        }

        foreach (var channel in state.Subscribers.Values)
        {
            channel.Writer.TryComplete();
        }
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

    private ConversationState GetRequired(string conversationId)
        => _conversations.TryGetValue(conversationId, out var state)
            ? state
            : throw new KeyNotFoundException($"Conversation introuvable : '{conversationId}'.");
}
