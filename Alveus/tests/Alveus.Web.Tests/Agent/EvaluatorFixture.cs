using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Alveus.Web.Tests.Agent;

/// <summary>
/// Reconstruit l'agent Alveus-Evaluator tel que défini dans Program.cs (mêmes tools,
/// même configuration llama.cpp — cf. ADR 0006), dans un workspace temporaire dédié et
/// isolé de celui du worker — cf. ADR 0021.
/// <see cref="IsLlamaCppAvailable"/> permet aux tests d'intégration de se désactiver
/// proprement si aucun serveur llama.cpp n'écoute sur l'endpoint configuré.
/// </summary>
public sealed class EvaluatorFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEvaluator";

    private static readonly IReadOnlyList<string> SkillNames = ["verify", "playwright"];

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-evaluator-tests-").FullName;

    public CmdRunTool CmdRunTool { get; }

    public StrReplaceEditorTool EditorTool { get; }

    public FinishTool FinishTool { get; }

    public LoadSkillTool? LoadSkillTool { get; }

    public AIAgent Agent { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public EvaluatorFixture()
    {
        CmdRunTool = new CmdRunTool(WorkspaceRoot);
        EditorTool = new StrReplaceEditorTool(WorkspaceRoot);
        FinishTool = new FinishTool();

        IChatClient chatClient = TestChatClientFactory.Create();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(CmdRunTool.RunAsync),
            AIFunctionFactory.Create(EditorTool.texteditor),
            AIFunctionFactory.Create(FinishTool.Finish),
        };
        var contextProviders = new List<AIContextProvider>();

        var skillsRoot = AgentSkillFiles.FindRoot(AppContext.BaseDirectory);
        if (skillsRoot is not null)
        {
            LoadSkillTool = new LoadSkillTool(skillsRoot, SkillNames);
            tools.Add(AIFunctionFactory.Create(LoadSkillTool.load_skill));
            contextProviders.Add(new SkillsContextProvider(skillsRoot, SkillNames));
        }

        Agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions
            {
                Instructions = "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne "
                    + "de tâche que l'agent d'exécution (Alveus-Worker), complétée par les instructions "
                    + "d'utilisation de l'environnement local fournies par Alveus-EnvironmentManager (URL, ports, "
                    + "commandes d'exemple), mais dans ton propre espace de travail, séparé du sien. Ton rôle : à "
                    + "partir de cette consigne, écris un jeu de test (scripts, assertions) qui vérifie "
                    + "objectivement que l'environnement décrit par les instructions d'utilisation répond à la "
                    + "consigne. Ton espace de travail est vide au départ — initialise-le selon les besoins "
                    + "(ex. 'dotnet new xunit' pour un projet C#, ou un script bash pour des assertions curl). "
                    + "Écris le jeu de test avec ton outil d'édition de fichiers, puis exécute-le avec ton outil "
                    + "shell en interagissant avec l'environnement uniquement par le réseau (ex. curl) — tu n'as "
                    + "pas accès au système de fichiers du Worker. N'effectue pas la tâche toi-même. Quand tu "
                    + "arrêtes de travailler, tu DOIS appeler l'outil Finish avec outcome='pass' "
                    + "si le jeu de test confirme que l'environnement répond à la consigne ; "
                    + "outcome='fail' si ce n'est pas le cas (reason=rapport détaillé des problèmes rencontrés, "
                    + "transmis à Alveus-Worker pour correction) ; outcome='needmoreinfo' si tu ne peux pas "
                    + "trancher sans information supplémentaire (reason et questions). Si tu es bloqué avant "
                    + "d'avoir pu écrire ou exécuter le jeu de test, utilise outcome='blocked' (reason) — "
                    + "sinon on te redemandera de le faire.",
                Tools = tools,
            },
            AIContextProviders = [.. contextProviders],
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
        CmdRunTool.Dispose();
        Directory.Delete(WorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
