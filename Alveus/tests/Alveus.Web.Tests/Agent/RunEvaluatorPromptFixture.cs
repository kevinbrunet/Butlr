using System.ClientModel;
using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Construit un conteneur DI minimal (Elsa workflow management + runtime, agent
/// Alveus-Evaluator) permettant d'exécuter <see cref="RunEvaluatorPrompt"/> via
/// <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR 0021. Contrairement à
/// <see cref="RunAgentPromptVerificationFixture"/>, aucune <see cref="IAgentWorkVerificationService"/>
/// n'est nécessaire : <see cref="RunEvaluatorPrompt"/> ne vérifie pas son propre travail.
/// </summary>
public sealed class RunEvaluatorPromptFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEvaluator";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-evaluator-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static Uri Endpoint => new(Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_ENDPOINT") ?? "http://127.0.0.1:8083/v1");

    private static string Model => Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_MODEL") ?? "qwen2.5-7b-instruct";

    public RunEvaluatorPromptFixture()
    {
        EvaluatorSkills.CopyInto(WorkspaceRoot, AppContext.BaseDirectory);

        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<RunEvaluatorPrompt>());
            elsa.UseWorkflowRuntime();
        });

        var openAiClient = new OpenAIClient(new ApiKeyCredential("not-needed"), new OpenAIClientOptions
        {
            Endpoint = Endpoint,
        });

        IChatClient chatClient = openAiClient.GetChatClient(Model).AsIChatClient();

        services.AddSingleton(_ => new CmdRunTool(WorkspaceRoot));
        services.AddSingleton(_ => new StrReplaceEditorTool(WorkspaceRoot));
        services.AddSingleton<FinishTool>();
        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();

        services.AddKeyedSingleton<AIAgent>(AgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredService<CmdRunTool>();
            var editorTool = sp.GetRequiredService<StrReplaceEditorTool>();
            var finishTool = sp.GetRequiredService<FinishTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.Execute),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne "
                    + "de tâche que l'agent d'exécution (Alveus-Worker), mais dans ton propre espace de travail, "
                    + "séparé du sien. Ton rôle : à partir de cette consigne, écris un jeu de test (scripts, "
                    + "assertions) qui permettrait de vérifier objectivement qu'un travail répondant à la consigne "
                    + "est correct, en l'écrivant avec ton outil d'édition de fichiers dans ton espace de travail. "
                    + "N'effectue pas la tâche toi-même. Ton espace de travail contient un dossier 'skills/' avec "
                    + "des méthodologies de référence (par ex. skills/dotnet-snapshot-testing/SKILL.md pour les "
                    + "tests de non-régression .NET par snapshot/approval testing) : consulte-les si la consigne "
                    + "s'y prête. Quand tu arrêtes de travailler (jeu de test écrit, besoin de précisions, ou "
                    + "bloqué), tu DOIS appeler l'outil Finish pour le signaler — sinon on te redemandera de le "
                    + "faire.",
                name: AgentName,
                tools: tools);
        });

        Services = services.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            // ~ llama.cpp expose /v1/models (API OpenAI-compatible) — utilisé ici uniquement
            // comme sonde de disponibilité, pas pour vérifier le contenu de la réponse.
            using var response = await client.GetAsync(new Uri($"{Endpoint.ToString().TrimEnd('/')}/models"));
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
        Directory.Delete(WorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
