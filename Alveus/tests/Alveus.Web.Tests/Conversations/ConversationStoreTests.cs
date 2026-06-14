using Alveus.Web.Conversations;

namespace Alveus.Web.Tests.Conversations;

/// <summary>
/// Test de <see cref="ConversationStore"/> (cf. ADR 0027) : sans LLM, sans Elsa.
/// </summary>
public sealed class ConversationStoreTests
{
    [Fact]
    public void Create_ReturnsConversation_WithRunningStatus()
    {
        var store = new ConversationStore();

        var conversation = store.Create();

        Assert.Equal("running", conversation.Status);
        Assert.Same(conversation, store.Get(conversation.Id));
    }

    [Fact]
    public void AddItem_And_GetItems_PreserveOrder()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        store.AddItem(conversation.Id, "user", "premier", ConversationItemKind.UserMessage);
        store.AddItem(conversation.Id, "assistant", "second", ConversationItemKind.ActivityTransition);

        var items = store.GetItems(conversation.Id);

        Assert.Equal(2, items.Count);
        Assert.Equal("premier", items[0].Text);
        Assert.Equal("second", items[1].Text);
    }

    [Fact]
    public void GetItems_WithAfter_ReturnsOnlyLaterItems()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        var first = store.AddItem(conversation.Id, "user", "premier", ConversationItemKind.UserMessage);
        store.AddItem(conversation.Id, "assistant", "second", ConversationItemKind.ActivityTransition);
        store.AddItem(conversation.Id, "assistant", "troisième", ConversationItemKind.ActivityTransition);

        var items = store.GetItems(conversation.Id, after: first.Id);

        Assert.Equal(2, items.Count);
        Assert.Equal("second", items[0].Text);
        Assert.Equal("troisième", items[1].Text);
    }

    [Fact]
    public void GetItems_WithLimit_TruncatesResults()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        store.AddItem(conversation.Id, "user", "premier", ConversationItemKind.UserMessage);
        store.AddItem(conversation.Id, "assistant", "second", ConversationItemKind.ActivityTransition);

        var items = store.GetItems(conversation.Id, limit: 1);

        var item = Assert.Single(items);
        Assert.Equal("premier", item.Text);
    }

    [Fact]
    public void SetPendingBookmark_SetsAwaitingInput_AndTryResolveResetsStatus()
    {
        var store = new ConversationStore();
        var conversation = store.Create();
        store.SetWorkflowInstanceId(conversation.Id, "wf-1");

        store.SetPendingBookmark(conversation.Id, "bookmark-1");

        Assert.Equal("awaiting_input", store.Get(conversation.Id)!.Status);

        var pending = store.TryResolvePendingBookmark(conversation.Id);

        Assert.Equal(("wf-1", "bookmark-1"), pending);
        Assert.Equal("running", store.Get(conversation.Id)!.Status);
    }

    [Fact]
    public void TryResolvePendingBookmark_WithoutPendingBookmark_ReturnsNull()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        Assert.Null(store.TryResolvePendingBookmark(conversation.Id));
    }

    [Fact]
    public void Complete_SetsStatus_AndCompletesSubscribers()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        store.Complete(conversation.Id);

        Assert.Equal("completed", store.Get(conversation.Id)!.Status);
    }

    [Fact]
    public void Complete_WithFailed_SetsFailedStatus()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        store.Complete(conversation.Id, failed: true);

        Assert.Equal("failed", store.Get(conversation.Id)!.Status);
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesItemsAddedAfterSubscription()
    {
        var store = new ConversationStore();
        var conversation = store.Create();

        using var cts = new CancellationTokenSource();
        var subscription = store.SubscribeAsync(conversation.Id, cts.Token);
        var enumerator = subscription.GetAsyncEnumerator(cts.Token);

        // Le premier MoveNextAsync n'avancera qu'une fois un item posté.
        var moveNextTask = enumerator.MoveNextAsync();

        store.AddItem(conversation.Id, "assistant", "diffusé", ConversationItemKind.ActivityTransition);

        Assert.True(await moveNextTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("diffusé", enumerator.Current.Text);

        await cts.CancelAsync();
    }
}
