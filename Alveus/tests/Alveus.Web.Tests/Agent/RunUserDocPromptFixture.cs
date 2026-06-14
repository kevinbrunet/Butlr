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
/// Alveus-UserDoc) permettant d'exécuter <see cref="RunUserDocPrompt"/> via
/// <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR 0026. Agent volontairement minimal
/// (pas de vérification ADR 0020), même schéma que <see cref="Agent.RunEvaluatorPromptFixture"/>.
/// </summary>
public sealed class RunUserDocPromptFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusUserDoc";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-userdoc-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static Uri Endpoint => new(Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_ENDPOINT") ?? "http://127.0.0.1:8083/v1");

    private static string Model => Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_MODEL") ?? "qwen2.5-7b-instruct";

    public RunUserDocPromptFixture()
    {
        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<RunUserDocPrompt>());
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
                instructions: "Tu es Alveus-UserDoc, l'agent de documentation utilisateur de Butlr. Ton rôle : "
                    + "mettre à jour la documentation utilisateur (markdown, à la racine de ton espace de travail) "
                    + "pour refléter ce qui change pour l'utilisateur final. Quand tu as terminé, appelle l'outil "
                    + "Finish avec outcome='done' (summary = ce qui a été documenté) ou "
                    + "outcome='needsmoreinfo'/'blocked' si tu ne peux pas avancer.",
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
