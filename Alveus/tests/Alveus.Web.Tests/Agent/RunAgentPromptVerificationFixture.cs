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
/// Construit un conteneur DI minimal (Elsa workflow management + runtime, agent Alveus-Worker,
/// services injectés dans <see cref="RunAgentPrompt"/>) avec un <c>Agent:VerificationCommand</c>
/// configuré — cf. ADR 0020. La commande échoue au premier appel puis réussit, ce qui démontre
/// la boucle de relance sur échec de vérification indépendamment du comportement du modèle.
/// </summary>
public sealed class RunAgentPromptVerificationFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusWorker";

    public const string VerificationCounterFileName = "verification-counter";

    private const string VerificationCommand =
        "c=$(cat " + VerificationCounterFileName + " 2>/dev/null || echo 0); c=$((c+1)); "
        + "echo $c > " + VerificationCounterFileName + "; "
        + "if [ \"$c\" -lt 2 ]; then echo verification-pas-encore-prete; exit 1; else echo verification-ok; exit 0; fi";

    public string WorkerWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public RunAgentPromptVerificationFixture()
    {
        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<RunAgentPrompt>());
            elsa.UseWorkflowRuntime();
        });

        IChatClient chatClient = TestChatClientFactory.Create();

        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
        services.AddSingleton(_ => new CmdRunTool(WorkerWorkspaceRoot));
        services.AddSingleton(_ => new StrReplaceEditorTool(WorkerWorkspaceRoot));
        services.AddSingleton<FinishTool>();
        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();
        services.AddSingleton<IAgentWorkVerificationService>(_ => new CmdAgentWorkVerificationService(WorkerWorkspaceRoot, VerificationCommand));

        services.AddKeyedSingleton<AIAgent>(AgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredService<CmdRunTool>();
            var editorTool = sp.GetRequiredService<StrReplaceEditorTool>();
            var finishTool = sp.GetRequiredService<FinishTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.texteditor),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-Worker, l'agent d'exécution technique de Butlr. Réponds de façon concise. "
                    + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
                    + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.",
                name: AgentName,
                tools: tools);
        });

        Services = services.BuildServiceProvider();
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
        Services.GetRequiredService<CmdRunTool>().Dispose();
        Directory.Delete(WorkerWorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
