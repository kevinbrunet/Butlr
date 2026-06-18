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
/// Construit un conteneur DI minimal (Elsa workflow management + runtime, agent
/// Alveus-Evaluator) permettant d'exécuter <see cref="RunEvaluatorPrompt"/> via
/// <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR 0021. Contrairement à
/// <see cref="RunAgentPromptVerificationFixture"/>, aucune <see cref="IAgentWorkVerificationService"/>
/// n'est nécessaire : <see cref="RunEvaluatorPrompt"/> ne vérifie pas son propre travail.
/// </summary>
public sealed class RunEvaluatorPromptFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEvaluator";

    private static readonly IReadOnlyList<string> SkillNames = ["verify", "playwright"];

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-evaluator-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public RunEvaluatorPromptFixture()
    {
        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<RunEvaluatorPrompt>());
            elsa.UseWorkflowRuntime();
        });

        IChatClient chatClient = TestChatClientFactory.Create();

        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
        services.AddSingleton(_ => new CmdRunTool(WorkspaceRoot));
        services.AddSingleton(_ => new StrReplaceEditorTool(WorkspaceRoot));
        services.AddSingleton<FinishTool>();

        var skillsRoot = AgentSkillFiles.FindRoot(AppContext.BaseDirectory);
        if (skillsRoot is not null)
            services.AddSingleton(_ => new LoadSkillTool(skillsRoot, SkillNames));

        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();

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
            var contextProviders = new List<AIContextProvider>();

            if (skillsRoot is not null)
            {
                var loadSkillTool = sp.GetRequiredService<LoadSkillTool>();
                tools.Add(AIFunctionFactory.Create(loadSkillTool.load_skill));
                contextProviders.Add(new SkillsContextProvider(skillsRoot, SkillNames));
            }

            return new ChatClientAgent(chatClient, new ChatClientAgentOptions
            {
                Name = AgentName,
                ChatOptions = new ChatOptions
                {
                    Instructions = "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même "
                        + "consigne de tâche que l'agent d'exécution (Alveus-Worker), complétée par les instructions "
                        + "d'utilisation de l'environnement local fournies par Alveus-EnvironmentManager (URL, "
                        + "ports, commandes d'exemple), mais dans ton propre espace de travail, séparé du sien. Ton "
                        + "rôle : à partir de cette consigne, écris un jeu de test (scripts, assertions) qui vérifie "
                        + "objectivement que l'environnement décrit par les instructions d'utilisation répond à la "
                        + "consigne. Ton espace de travail est vide au départ — initialise-le selon les besoins "
                        + "(ex. 'dotnet new xunit' pour un projet C#, ou un script bash pour des assertions curl). "
                        + "Écris le jeu de test avec ton outil d'édition de fichiers, puis exécute-le avec ton "
                        + "outil shell en interagissant avec l'environnement uniquement par le réseau (ex. curl) — "
                        + "tu n'as pas accès au système de fichiers du Worker. N'effectue pas la tâche toi-même. "
                        + "Quand tu arrêtes de travailler, tu DOIS appeler l'outil Finish avec outcome='pass' "
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
        Directory.Delete(WorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
