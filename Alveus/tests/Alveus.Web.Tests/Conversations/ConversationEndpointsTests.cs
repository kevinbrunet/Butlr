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

    private static async Task<ServiceProvider> BuildProviderAsync()
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
        await provider.GetRequiredService<IWorkflowDefinitionStorePopulator>()
            .PopulateStoreAsync(CancellationToken.None);
        return provider;
    }

    /// <summary>
    /// Vérifie que <see cref="ConversationEndpoints.CreateConversationAsync"/> retourne l'ID
    /// immédiatement sans attendre la fin du workflow — comportement introduit pour éviter de
    /// bloquer la connexion HTTP pendant toute la durée d'exécution (qui peut durer des minutes).
    /// À l'instant du retour, le statut est "running" : le workflow tourne en arrière-plan dans
    /// un scope DI indépendant (Task.Run).
    /// </summary>
    [Fact]
    public async Task CreateConversation_ReturnsBeforeWorkflowSuspends_StatusIsRunning()
    {
        await using var provider = await BuildProviderAsync();
        var store = provider.GetRequiredService<IConversationStore>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var result = await ConversationEndpoints.CreateConversationAsync(
            new CreateConversationRequest([
                new ConversationItemRequest("user", [new ConversationContentPart("input_text", "test")]),
            ]),
            "default", store, scopeFactory, CancellationToken.None);

        var created = Assert.IsType<ConversationResponse>(GetOkValue(result));

        Assert.Equal("running", created.Status);
    }

    [Fact]
    public async Task CreateConversation_WorkflowEventuallySuspends_OnAwaitConversationReply()
    {
        await using var provider = await BuildProviderAsync();
        var store = provider.GetRequiredService<IConversationStore>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();

        var createResult = await ConversationEndpoints.CreateConversationAsync(
            new CreateConversationRequest([
                new ConversationItemRequest("user", [new ConversationContentPart("input_text", "Bonjour")]),
            ]),
            "default", store, scopeFactory, CancellationToken.None);

        var created = Assert.IsType<ConversationResponse>(GetOkValue(createResult));

        // Le workflow tourne en background : attendre qu'il se suspende sur AwaitConversationReply.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (store.Get(created.Id)!.Status == "running" && !cts.IsCancellationRequested)
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { break; }

        Assert.Equal("awaiting_input", store.Get(created.Id)!.Status);

        var getResult = ConversationEndpoints.GetConversation(created.Id, store);
        var fetched = Assert.IsType<ConversationResponse>(GetOkValue(getResult));
        Assert.Equal(created.Id, fetched.Id);

        var itemsResult = ConversationEndpoints.ListConversationItems(created.Id, null, null, store);
        var itemsResponse = Assert.IsType<ConversationItemListResponse>(GetOkValue(itemsResult));

        Assert.Contains(itemsResponse.Data, i => i.Metadata["kind"] == "user_message" && i.Content[0].Text == "Bonjour");
        Assert.Contains(itemsResponse.Data, i => i.Metadata["kind"] == "needs_help_question");

        var addResult = await ConversationEndpoints.AddConversationItemsAsync(
            created.Id,
            new AddConversationItemsRequest([
                new ConversationItemRequest("user", [new ConversationContentPart("input_text", "ma réponse")]),
            ]),
            store, runtime, CancellationToken.None);

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
