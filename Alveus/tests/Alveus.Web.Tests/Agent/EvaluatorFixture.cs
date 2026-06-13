using System.ClientModel;
using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Reconstruit l'agent Alveus-Evaluator tel que défini dans Program.cs (mêmes tools que
/// Alveus-Worker, même configuration llama.cpp — cf. ADR 0006), dans un workspace temporaire
/// dédié et isolé de celui du worker — cf. ADR 0021.
/// <see cref="IsLlamaCppAvailable"/> permet aux tests d'intégration de se désactiver
/// proprement si aucun serveur llama.cpp n'écoute sur l'endpoint configuré.
/// </summary>
public sealed class EvaluatorFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEvaluator";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-evaluator-tests-").FullName;

    public CmdRunTool CmdRunTool { get; }

    public StrReplaceEditorTool EditorTool { get; }

    public FinishTool FinishTool { get; }

    public AIAgent Agent { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static Uri Endpoint => new(Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_ENDPOINT") ?? "http://127.0.0.1:8083/v1");

    private static string Model => Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_MODEL") ?? "qwen2.5-7b-instruct";

    public EvaluatorFixture()
    {
        EvaluatorSkills.CopyInto(WorkspaceRoot, AppContext.BaseDirectory);

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
            instructions: "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne de "
                + "tâche que l'agent d'exécution (Alveus-Worker), mais dans ton propre espace de travail, séparé du "
                + "sien. Ton rôle : à partir de cette consigne, écris un jeu de test (scripts, assertions) qui "
                + "permettrait de vérifier objectivement qu'un travail répondant à la consigne est correct, en "
                + "l'écrivant avec ton outil d'édition de fichiers dans ton espace de travail. N'effectue pas la "
                + "tâche toi-même. Ton espace de travail contient un dossier 'skills/' avec des méthodologies de "
                + "référence (par ex. skills/dotnet-snapshot-testing/SKILL.md pour les tests de non-régression "
                + ".NET par snapshot/approval testing) : consulte-les si la consigne s'y prête. Quand tu arrêtes "
                + "de travailler (jeu de test écrit, besoin de précisions, ou bloqué), tu DOIS appeler l'outil "
                + "Finish pour le signaler — sinon on te redemandera de le faire.",
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
