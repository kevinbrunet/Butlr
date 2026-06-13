using System.ClientModel;
using Alveus.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Reconstruit l'agent Alveus-Worker tel que défini dans Program.cs (mêmes tools, même
/// configuration llama.cpp — cf. ADR 0006), dans un workspace temporaire dédié.
/// <see cref="IsLlamaCppAvailable"/> permet aux tests d'intégration de se désactiver
/// proprement si aucun serveur llama.cpp n'écoute sur l'endpoint configuré.
/// </summary>
public sealed class AgentFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusWorker";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-agent-tests-").FullName;

    public CmdRunTool CmdRunTool { get; }

    public StrReplaceEditorTool EditorTool { get; }

    public FinishTool FinishTool { get; }

    public AIAgent Agent { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static Uri Endpoint => new(Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_ENDPOINT") ?? "http://127.0.0.1:8083/v1");

    private static string Model => Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_MODEL") ?? "qwen2.5-7b-instruct";

    public AgentFixture()
    {
        CmdRunTool = new CmdRunTool(WorkspaceRoot);
        EditorTool = new StrReplaceEditorTool(WorkspaceRoot);
        FinishTool = new FinishTool();

        var openAiClient = new OpenAIClient(new ApiKeyCredential("not-needed"), new OpenAIClientOptions
        {
            Endpoint = Endpoint,
        });

        IChatClient chatClient = openAiClient.GetChatClient(Model).AsIChatClient();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(CmdRunTool.RunAsync),
            AIFunctionFactory.Create(EditorTool.Execute),
            AIFunctionFactory.Create(FinishTool.Finish),
        };

        Agent = new ChatClientAgent(
            chatClient,
            instructions: "Tu es Alveus-Worker, l'agent d'exécution technique de Butlr. Réponds de façon concise. "
                + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
                + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.",
            name: AgentName,
            tools: tools);
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
        CmdRunTool.Dispose();
        Directory.Delete(WorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
