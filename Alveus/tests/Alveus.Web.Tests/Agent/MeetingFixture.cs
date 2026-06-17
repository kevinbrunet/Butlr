using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Conversations;
using Alveus.Web.Tools;
using Elsa.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Construit un conteneur DI minimal (Elsa workflow management + runtime, agents
/// Alveus-BusinessAnalyst/Alveus-Qa/Alveus-Technical outillés avec <see cref="FinishTool"/> et
/// <see cref="MeetingTool"/>) permettant d'exécuter <see cref="RunPreTaskMeeting"/> et
/// <see cref="RunFinalReviewMeeting"/> via <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR
/// 0024. Chaque agent a son propre espace de travail temporaire (les relations d'imbrication
/// décrites par ADR 0025 ne sont pas nécessaires pour ces tests, qui portent sur l'orchestration
/// de la réunion, pas sur le contenu des workspaces).
/// </summary>
public sealed class MeetingFixture : IAsyncLifetime
{
    // Nom d'équipe utilisé dans les tests de réunion (cf. ADR 0031).
    public const string TeamName = "test";
    public const string BusinessAnalystAgentName = $"{TeamName}:BusinessAnalyst";
    public const string QaAgentName = $"{TeamName}:Qa";
    public const string TechnicalAgentName = $"{TeamName}:Technical";

    public string BusinessAnalystWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-meeting-ba-tests-").FullName;

    public string QaWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-meeting-qa-tests-").FullName;

    public string TechnicalWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-meeting-tech-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public MeetingFixture()
    {
        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management =>
            {
                management.AddActivity<RunPreTaskMeeting>();
                management.AddActivity<RunFinalReviewMeeting>();
            });
            elsa.UseWorkflowRuntime();
        });

        IChatClient chatClient = TestChatClientFactory.Create();

        // MeetingActivityBase accède à IConversationContextAccessor (et conditionnellement IConversationStore).
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();

        services.AddSingleton<FinishTool>();
        services.AddSingleton<MeetingTool>();
        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();

        AddMeetingAgent(services, chatClient, BusinessAnalystAgentName, BusinessAnalystWorkspaceRoot, "Alveus-BusinessAnalyst");
        AddMeetingAgent(services, chatClient, QaAgentName, QaWorkspaceRoot, "Alveus-Qa");
        AddMeetingAgent(services, chatClient, TechnicalAgentName, TechnicalWorkspaceRoot, "Alveus-Technical");

        Services = services.BuildServiceProvider();
    }

    private static void AddMeetingAgent(ServiceCollection services, IChatClient chatClient, string agentName, string workspaceRoot, string displayName)
    {
        services.AddKeyedSingleton<CmdRunTool>(agentName, (_, _) => new CmdRunTool(workspaceRoot));
        services.AddKeyedSingleton<StrReplaceEditorTool>(agentName, (_, _) => new StrReplaceEditorTool(workspaceRoot));

        services.AddKeyedSingleton<AIAgent>(agentName, (sp, key) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
            var editorTool = sp.GetRequiredKeyedService<StrReplaceEditorTool>(key);
            var finishTool = sp.GetRequiredService<FinishTool>();
            var meetingTool = sp.GetRequiredService<MeetingTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.texteditor),
                AIFunctionFactory.Create(finishTool.Finish),
                AIFunctionFactory.Create(meetingTool.Raise),
                AIFunctionFactory.Create(meetingTool.Vote),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: $"Tu es {displayName}, un participant aux réunions de Butlr. Tu disposes des outils "
                    + "Raise (signaler un point de désaccord ou une question aux 2 autres participants) et Vote "
                    + "(te positionner sur un topic, 'agree'/'disagree', commentaire obligatoire si 'disagree'). "
                    + "Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='done' ou "
                    + "outcome='needsmoreinfo'/'blocked' si tu es bloqué.",
                name: agentName,
                tools: tools);
        });
    }

    public async Task InitializeAsync()
    {
        var endpoint = TestLlamaCppConfig.Endpoint;
        if (endpoint is null)
            return;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            // ~ llama.cpp expose /v1/models (API OpenAI-compatible) — utilisé ici uniquement
            // comme sonde de disponibilité, pas pour vérifier le contenu de la réponse.
            using var response = await client.GetAsync(new Uri($"{endpoint.TrimEnd('/')}/models"));
            IsLlamaCppAvailable = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsLlamaCppAvailable = false;
        }
    }

    public Task DisposeAsync()
    {
        Services.GetRequiredKeyedService<CmdRunTool>(BusinessAnalystAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(QaAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(TechnicalAgentName).Dispose();
        Directory.Delete(BusinessAnalystWorkspaceRoot, recursive: true);
        Directory.Delete(QaWorkspaceRoot, recursive: true);
        Directory.Delete(TechnicalWorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
