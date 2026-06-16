using Alveus.Web.Activities;
using Alveus.Web.Conversations;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Tests.Conversations;

/// <summary>
/// Test de contrat de <see cref="ConversationEndpoints"/> (cf. ADR 0027) : appelle directement les
/// gestionnaires d'endpoints (rendus <c>internal</c>, cf. <c>InternalsVisibleTo</c>) avec un workflow
/// minimal enregistré sous l'identifiant <see cref="ConversationEndpoints.WorkflowDefinitionId"/> —
/// même structure que <see cref="Activities.AwaitConversationReplyTests"/>, mais en passant par
/// l'API HTTP plutôt que directement par <see cref="IWorkflowRuntime"/>. Sans LLM.
/// </summary>
public sealed class ConversationEndpointsTests
{
    private sealed class TestTaskWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.WithDefinitionId(ConversationEndpoints.WorkflowDefinitionId);
            builder.WithInput("TaskPrompt", typeof(string), "Consigne initiale de la tâche.");
            builder.WithInput("ConversationId", typeof(string), "Identifiant de la conversation associée.");

            var awaitReply = new AwaitConversationReply
            {
                Id = "AwaitReply",
                SourceLabel = new Input<string>("Test"),
            };

            builder.Root = new Flowchart { Start = awaitReply };
        }
    }

    [Fact]
    public async Task CreateConversation_StartsWorkflow_AndSuspendsOnAwaitConversationReply()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<AwaitConversationReply>());
            elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<TestTaskWorkflow>());
        });

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IConversationStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();

        var populator = provider.GetRequiredService<IWorkflowDefinitionStorePopulator>();
        await populator.PopulateStoreAsync(CancellationToken.None);

        var createRequest = new CreateConversationRequest(
        [
            new ConversationItemRequest("user", [new ConversationContentPart("input_text", "Bonjour")]),
        ]);

        var createResult = await ConversationEndpoints.CreateConversationAsync(createRequest, "default", store, runtime, CancellationToken.None);
        var created = Assert.IsType<ConversationResponse>(GetOkValue(createResult));

        Assert.Equal("awaiting_input", created.Status);

        var getResult = ConversationEndpoints.GetConversation(created.Id, store);
        var fetched = Assert.IsType<ConversationResponse>(GetOkValue(getResult));
        Assert.Equal(created.Id, fetched.Id);

        var itemsResult = ConversationEndpoints.ListConversationItems(created.Id, null, null, store);
        var itemsResponse = Assert.IsType<ConversationItemListResponse>(GetOkValue(itemsResult));

        Assert.Contains(itemsResponse.Data, i => i.Metadata["kind"] == "user_message" && i.Content[0].Text == "Bonjour");
        Assert.Contains(itemsResponse.Data, i => i.Metadata["kind"] == "needs_help_question");

        var addRequest = new AddConversationItemsRequest(
        [
            new ConversationItemRequest("user", [new ConversationContentPart("input_text", "ma réponse")]),
        ]);

        var addResult = await ConversationEndpoints.AddConversationItemsAsync(created.Id, addRequest, store, runtime, CancellationToken.None);
        Assert.IsAssignableFrom<IResult>(addResult);

        Assert.Equal("completed", store.Get(created.Id)!.Status);

        var itemsAfterReply = ConversationEndpoints.ListConversationItems(created.Id, null, null, store);
        var itemsAfterReplyResponse = Assert.IsType<ConversationItemListResponse>(GetOkValue(itemsAfterReply));

        Assert.Contains(itemsAfterReplyResponse.Data, i => i.Metadata["kind"] == "human_reply" && i.Content[0].Text == "ma réponse");
    }

    [Fact]
    public void GetConversation_UnknownId_ReturnsNotFound()
    {
        var store = new ConversationStore();

        var result = ConversationEndpoints.GetConversation("unknown", store);

        Assert.IsAssignableFrom<IResult>(result);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.Ok<ConversationResponse>>(result);
    }

    private static object? GetOkValue(IResult result)
    {
        var valueProperty = result.GetType().GetProperty("Value");
        return valueProperty?.GetValue(result);
    }
}
