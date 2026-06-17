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
/// Alveus-EnvironmentManager) permettant d'exécuter <see cref="RunEnvironmentPrompt"/> via
/// <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR 0023. Contrairement à
/// <see cref="EvaluatorFixture"/>, l'EnvironmentManager partage le même workspace et les mêmes
/// outils que le Worker (pas d'isolation).
/// </summary>
public sealed class RunEnvironmentPromptFixture : IAsyncLifetime
{
    private const string AgentName = "AlveusEnvironmentManager";

    public string WorkerWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-envmanager-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    public RunEnvironmentPromptFixture()
    {
        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.AddActivity<RunEnvironmentPrompt>());
            elsa.UseWorkflowRuntime();
        });

        IChatClient chatClient = TestChatClientFactory.Create();

        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
        services.AddSingleton(_ => new CmdRunTool(WorkerWorkspaceRoot));
        services.AddSingleton(_ => new StrReplaceEditorTool(WorkerWorkspaceRoot));
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
                AIFunctionFactory.Create(editorTool.texteditor),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-EnvironmentManager, l'agent de gestion d'environnement de Butlr. Tu "
                    + "interviens après que l'agent d'exécution (Alveus-Worker) a terminé sa tâche, dans le même "
                    + "espace de travail et avec les mêmes outils que lui. Ton rôle : lancer ou relancer "
                    + "l'environnement local décrit par la consigne pour qu'il soit utilisable par un autre agent. "
                    + "Ton outil shell a un timeout de 30 secondes : lance les processus longue durée en arrière-plan "
                    + "(ex. 'nohup <commande> > /tmp/env.log 2>&1 & disown') plutôt qu'au premier plan. Quand tu "
                    + "arrêtes de travailler, appelle l'outil Finish avec outcome='done' et : verdict='pass' si "
                    + "l'environnement est démarré — résume alors dans summary des instructions d'utilisation "
                    + "précises (URL, ports, exemples de requêtes ou de commandes) destinées à un autre agent qui "
                    + "n'a pas accès à ce système de fichiers ; verdict='fail' si le démarrage échoue (reason=détail "
                    + "de l'échec) ; verdict='needmoreinfo' si la consigne ne précise pas comment démarrer "
                    + "l'environnement (reason et questions).",
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
