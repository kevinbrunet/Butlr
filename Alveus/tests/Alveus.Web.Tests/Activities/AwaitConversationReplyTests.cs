using Alveus.Web.Activities;
using Alveus.Web.Conversations;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Tests.Activities;

/// <summary>
/// Test déterministe (sans LLM) de <see cref="AwaitConversationReply"/> (cf. ADR 0027) : valide le
/// cycle complet création de bookmark / suspension / reprise via <see cref="IWorkflowRuntime"/>.
/// </summary>
public sealed class AwaitConversationReplyTests
{
    private sealed class AwaitReplyWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId("AwaitReplyWorkflow");
            builder.WithInput("ConversationId", typeof(string), "Identifiant de conversation.");

            var awaitReply = new AwaitConversationReply
            {
                Id = "AwaitReply",
                SourceLabel = new Input<string>("Test"),
            };

            builder.Root = new Flowchart { Start = awaitReply };
        }
    }

    [Fact]
    public async Task AwaitConversationReply_SuspendsThenResumes_WithHumanReply()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<AwaitConversationReply>());
            elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<AwaitReplyWorkflow>());
        });

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IConversationStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();

        var populator = provider.GetRequiredService<IWorkflowDefinitionStorePopulator>();
        await populator.PopulateStoreAsync(CancellationToken.None);

        var conversation = store.Create();

        var client = await runtime.CreateClientAsync(CancellationToken.None);
        await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("AwaitReplyWorkflow"),
            CorrelationId = conversation.Id,
            Input = new Dictionary<string, object> { ["ConversationId"] = conversation.Id },
        }, CancellationToken.None);

        var runResponse = await client.RunInstanceAsync(new RunWorkflowInstanceRequest(), CancellationToken.None);

        Assert.NotEmpty(runResponse.Bookmarks);

        var itemsAfterStart = store.GetItems(conversation.Id);
        Assert.Contains(itemsAfterStart, i => i.Kind == ConversationItemKind.NeedsHelpQuestion);
        Assert.Equal("awaiting_input", store.Get(conversation.Id)!.Status);

        store.SetWorkflowInstanceId(conversation.Id, client.WorkflowInstanceId);

        var pending = store.TryResolvePendingBookmark(conversation.Id);
        Assert.NotNull(pending);

        var resumeClient = await runtime.CreateClientAsync(pending!.Value.WorkflowInstanceId, CancellationToken.None);
        var resumeResponse = await resumeClient.RunInstanceAsync(new RunWorkflowInstanceRequest
        {
            BookmarkId = pending.Value.BookmarkId,
            Input = new Dictionary<string, object> { ["Reply"] = "hello" },
        }, CancellationToken.None);

        Assert.Equal(WorkflowSubStatus.Finished, resumeResponse.SubStatus);

        var itemsAfterResume = store.GetItems(conversation.Id);
        Assert.Contains(itemsAfterResume, i => i.Kind == ConversationItemKind.HumanReply && i.Text == "hello");
        Assert.Equal("running", store.Get(conversation.Id)!.Status);
    }
}
