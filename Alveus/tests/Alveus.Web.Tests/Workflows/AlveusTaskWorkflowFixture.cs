using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Conversations;
using Alveus.Web.Tools;
using Alveus.Web.Workflows;
using Elsa.Extensions;
using Elsa.Workflows.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Tests.Workflows;

/// <summary>
/// Construit un conteneur DI complet (Elsa workflow management + runtime, agents
/// Alveus-Worker/Alveus-EnvironmentManager/Alveus-Evaluator) permettant d'exécuter
/// <see cref="AlveusTaskWorkflow"/> via <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR
/// 0023. Alveus-Worker et Alveus-EnvironmentManager partagent le même workspace et les mêmes
/// outils (<see cref="WorkerWorkspaceRoot"/>) ; Alveus-Evaluator a son propre workspace isolé
/// (<see cref="EvaluatorWorkspaceRoot"/>), cf. ADR 0021.
/// </summary>
public sealed class AlveusTaskWorkflowFixture : IAsyncLifetime
{
    // Toutes les clés DI sont préfixées par le nom d'équipe (cf. ADR 0031).
    internal const string TeamName = "default";
    private const string WorkerAgentName = $"{TeamName}:Worker";
    private const string EnvironmentManagerAgentName = $"{TeamName}:EnvironmentManager";
    private const string EvaluatorAgentName = $"{TeamName}:Evaluator";
    private const string UserDocAgentName = $"{TeamName}:UserDoc";
    private const string BusinessAnalystAgentName = $"{TeamName}:BusinessAnalyst";
    private const string QaAgentName = $"{TeamName}:Qa";
    private const string TechnicalAgentName = $"{TeamName}:Technical";

    public string WorkerWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-tests-").FullName;

    public string EvaluatorWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-evaluator-tests-").FullName;

    public string UserDocWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-userdoc-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static readonly IReadOnlyList<string> EvaluatorSkillNames = ["verify", "playwright"];

    public AlveusTaskWorkflowFixture()
    {

        var services = new ServiceCollection();

        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management =>
            {
                management.AddActivity<RunAgentPrompt>();
                management.AddActivity<RunEnvironmentPrompt>();
                management.AddActivity<RunEvaluatorPrompt>();
                management.AddActivity<RunUserDocPrompt>();
                management.AddActivity<RunPreTaskMeeting>();
                management.AddActivity<RunFinalReviewMeeting>();
                management.AddActivity<AwaitConversationReply>();
            });
            elsa.UseWorkflowRuntime(runtime => runtime.AddWorkflow<AlveusTaskWorkflow>());
        });

        // API de conversation (cf. ADR 0027) : mêmes enregistrements que Program.cs.
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();

        // Configuration minimale requise par AlveusTaskWorkflow (Teams, cf. ADR 0031).
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Teams:0:Name"] = TeamName,
                [$"Teams:0:MissionPrompt"] = "Test.",
                [$"Teams:0:SpecialistRoles:0:Key"] = "BusinessAnalyst",
            })
            .Build();
        services.AddSingleton(configuration);

        IChatClient chatClient = TestChatClientFactory.Create();

        // Worker + EnvironmentManager : même workspace, mêmes outils (ADR 0023).
        // Timeout long pour les commandes dotnet (restore, build, new) qui peuvent dépasser 30s.
        var longCommandTimeout = TimeSpan.FromMinutes(3);
        services.AddKeyedSingleton<CmdRunTool>(WorkerAgentName, (_, _) => new CmdRunTool(WorkerWorkspaceRoot, commandTimeout: longCommandTimeout));
        services.AddKeyedSingleton<StrReplaceEditorTool>(WorkerAgentName, (_, _) => new StrReplaceEditorTool(WorkerWorkspaceRoot));
        services.AddSingleton<FinishTool>();
        services.AddSingleton<MeetingTool>();
        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();
        services.AddKeyedSingleton<IAgentWorkVerificationService>(TeamName, (_, _) => new CmdAgentWorkVerificationService(WorkerWorkspaceRoot, command: null));

        services.AddKeyedSingleton<AIAgent>(WorkerAgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(WorkerAgentName);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(WorkerAgentName),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-Worker");
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
                    + "Travaille exclusivement dans ton répertoire de travail courant (ton workspace) : crée tes "
                    + "sous-répertoires et fichiers là où tu te trouves, sans faire `cd /tmp`, `cd ~`, `cd /home` ou "
                    + "tout autre chemin absolu hors workspace. Si une commande échoue avec 'No such file or "
                    + "directory', vérifie que tu es bien dans le workspace (commande `pwd`) avant de réessayer. "
                    + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
                    + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.",
                name: WorkerAgentName,
                tools: tools);
        });

        services.AddKeyedSingleton<AIAgent>(EnvironmentManagerAgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(WorkerAgentName);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(WorkerAgentName),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-EnvironmentManager");
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
                    + "Ton outil shell a un timeout de 30 secondes : lance les processus longue durée en "
                    + "arrière-plan (ex. 'nohup <commande> > /tmp/env.log 2>&1 & disown') plutôt qu'au premier plan. "
                    + "Quand tu arrêtes de travailler, appelle l'outil Finish avec outcome='pass' "
                    + "si l'environnement est démarré — résume alors dans summary des instructions d'utilisation "
                    + "précises (URL, ports, exemples de requêtes ou de commandes) destinées à un autre agent qui "
                    + "n'a pas accès à ce système de fichiers ; outcome='fail' si le démarrage échoue (reason=détail "
                    + "de l'échec) ; outcome='needmoreinfo' si la consigne ne précise pas comment démarrer "
                    + "l'environnement (reason et questions).",
                name: EnvironmentManagerAgentName,
                tools: tools);
        });

        // Evaluator : workspace isolé (ADR 0021).
        var skillsRoot = AgentSkillFiles.FindRoot(AppContext.BaseDirectory);
        services.AddKeyedSingleton<CmdRunTool>(EvaluatorAgentName, (_, _) => new CmdRunTool(EvaluatorWorkspaceRoot, commandTimeout: longCommandTimeout));
        services.AddKeyedSingleton<StrReplaceEditorTool>(EvaluatorAgentName, (_, _) => new StrReplaceEditorTool(EvaluatorWorkspaceRoot));
        if (skillsRoot is not null)
            services.AddKeyedSingleton<LoadSkillTool>(EvaluatorAgentName, (_, _) => new LoadSkillTool(skillsRoot, EvaluatorSkillNames));

        services.AddKeyedSingleton<AIAgent>(EvaluatorAgentName, (sp, key) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-Evaluator");
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
                var loadSkillTool = sp.GetRequiredKeyedService<LoadSkillTool>(key);
                tools.Add(AIFunctionFactory.Create(loadSkillTool.load_skill));
                contextProviders.Add(new SkillsContextProvider(skillsRoot, EvaluatorSkillNames));
            }

            return new ChatClientAgent(chatClient, new ChatClientAgentOptions
            {
                Name = EvaluatorAgentName,
                ChatOptions = new ChatOptions
                {
                    Instructions = "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne de "
                        + "tâche que l'agent d'exécution (Alveus-Worker), complétée par les instructions d'utilisation de "
                        + "l'environnement local fournies par Alveus-EnvironmentManager (URL, ports, commandes d'exemple), "
                        + "mais dans ton propre espace de travail, séparé du sien. Ton rôle : à partir de cette consigne, "
                        + "écris un jeu de test (scripts, assertions) qui vérifie objectivement que l'environnement décrit "
                        + "par les instructions d'utilisation répond à la consigne. Ton espace de travail est vide au "
                        + "départ — initialise-le selon les besoins (ex. 'dotnet new xunit' pour un projet C#, ou un "
                        + "script bash pour des assertions curl). Travaille exclusivement dans ton répertoire de travail "
                        + "courant : ne fais pas `cd /tmp`, `cd ~` ou similaire — crée tes sous-répertoires et fichiers "
                        + "là où tu te trouves. Écris le jeu de test avec ton outil d'édition de "
                        + "fichiers, puis exécute-le avec ton outil shell en interagissant avec l'environnement "
                        + "uniquement par le réseau (ex. curl) — tu n'as pas accès au système de fichiers du Worker. "
                        + "N'effectue pas la tâche toi-même. Quand tu arrêtes de travailler, tu DOIS appeler l'outil "
                        + "Finish avec outcome='pass' si le jeu de test confirme que l'environnement "
                        + "répond à la consigne ; outcome='fail' si ce n'est pas le cas (reason=rapport détaillé des "
                        + "problèmes rencontrés, transmis à Alveus-Worker pour correction) ; outcome='needmoreinfo' si "
                        + "tu ne peux pas trancher sans information supplémentaire (reason et questions). Si tu es bloqué "
                        + "avant d'avoir pu écrire ou exécuter le jeu de test, utilise outcome='blocked' (reason) — "
                        + "sinon on te redemandera de le faire.",
                    Tools = tools,
                },
                AIContextProviders = [.. contextProviders],
            });
        });

        // UserDoc : workspace dédié (ADR 0026). Sous-dossier 'business-rules/' = workspace de BA (ADR 0025).
        services.AddKeyedSingleton<CmdRunTool>(UserDocAgentName, (_, _) => new CmdRunTool(UserDocWorkspaceRoot));
        services.AddKeyedSingleton<StrReplaceEditorTool>(UserDocAgentName, (_, _) => new StrReplaceEditorTool(UserDocWorkspaceRoot));

        services.AddKeyedSingleton<AIAgent>(UserDocAgentName, (sp, key) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-UserDoc");
            var finishTool = sp.GetRequiredService<FinishTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.texteditor),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-UserDoc, l'agent de documentation utilisateur de Butlr. Tu interviens "
                    + "après qu'Alveus-Evaluator a validé le travail d'Alveus-Worker. Ton rôle : mettre à jour la "
                    + "documentation utilisateur (markdown, à la racine de ton espace de travail) pour refléter ce "
                    + "qui change pour l'utilisateur final. Quand tu as terminé, appelle l'outil Finish avec "
                    + "outcome='pass' (summary = ce qui a été documenté) ou outcome='needmoreinfo'/'blocked' si tu "
                    + "ne peux pas avancer.",
                name: UserDocAgentName,
                tools: tools);
        });

        // Alveus-BusinessAnalyst/Alveus-Qa/Alveus-Technical : participants aux réunions (ADR 0024), espaces de
        // travail enracinés sur un sous-dossier respectivement de UserDoc, Evaluator et Worker (ADR 0025).
        var businessAnalystWorkspaceRoot = Path.Combine(UserDocWorkspaceRoot, "business-rules");
        Directory.CreateDirectory(businessAnalystWorkspaceRoot);
        var qaWorkspaceRoot = Path.Combine(EvaluatorWorkspaceRoot, "test-plan");
        Directory.CreateDirectory(qaWorkspaceRoot);
        var technicalWorkspaceRoot = Path.Combine(WorkerWorkspaceRoot, "tech-docs");
        Directory.CreateDirectory(technicalWorkspaceRoot);

        AddMeetingAgent(services, chatClient, BusinessAnalystAgentName, businessAnalystWorkspaceRoot, "Alveus-BusinessAnalyst");
        AddMeetingAgent(services, chatClient, QaAgentName, qaWorkspaceRoot, "Alveus-Qa");
        AddMeetingAgent(services, chatClient, TechnicalAgentName, technicalWorkspaceRoot, "Alveus-Technical");

        Services = services.BuildServiceProvider();
    }

    private static void AddMeetingAgent(ServiceCollection services, IChatClient chatClient, string agentName, string workspaceRoot, string displayName)
    {
        services.AddKeyedSingleton<CmdRunTool>(agentName, (_, _) => new CmdRunTool(workspaceRoot));
        services.AddKeyedSingleton<StrReplaceEditorTool>(agentName, (_, _) => new StrReplaceEditorTool(workspaceRoot));

        services.AddKeyedSingleton<AIAgent>(agentName, (sp, key) =>
        {
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                displayName);
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
                    + "Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='pass' ou "
                    + "outcome='needmoreinfo'/'blocked' si tu es bloqué.",
                name: agentName,
                tools: tools);
        });
    }

    public async Task InitializeAsync()
    {
        // Peupler le store Elsa pour que CreateClientAsync/CreateInstanceAsync(ByDefinitionId)
        // puisse résoudre la définition AlveusTaskWorkflow (cf. AwaitConversationReplyTests).
        var populator = Services.GetRequiredService<IWorkflowDefinitionStorePopulator>();
        await populator.PopulateStoreAsync(CancellationToken.None);

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

    // Remet le cwd du shell Worker sur son workspace root. À appeler au début des tests qui
    // utilisent le Worker, afin de neutraliser un éventuel `cd /tmp` laissé par le test précédent.
    public Task ResetWorkerShellCwdAsync(CancellationToken cancellationToken = default)
        => Services.GetRequiredKeyedService<CmdRunTool>(WorkerAgentName).ResetWorkingDirectoryAsync(cancellationToken);

    public Task DisposeAsync()
    {
        Services.GetRequiredKeyedService<CmdRunTool>(WorkerAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(EvaluatorAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(UserDocAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(BusinessAnalystAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(QaAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(TechnicalAgentName).Dispose();
        Directory.Delete(WorkerWorkspaceRoot, recursive: true);
        Directory.Delete(EvaluatorWorkspaceRoot, recursive: true);
        Directory.Delete(UserDocWorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
