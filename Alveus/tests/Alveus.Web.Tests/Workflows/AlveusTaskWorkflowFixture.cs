using System.ClientModel;
using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Conversations;
using Alveus.Web.Tools;
using Alveus.Web.Workflows;
using Elsa.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Alveus.Web.Tests.Workflows;

/// <summary>
/// Construit un conteneur DI complet (Elsa workflow management + runtime, agents
/// Alveus-Worker/Alveus-EnvironmentManager/Alveus-Evaluator) permettant d'exécuter
/// <see cref="AlveusTaskWorkflow"/> via <see cref="Elsa.Workflows.IWorkflowRunner"/> — cf. ADR
/// 0023. Alveus-Worker et Alveus-EnvironmentManager partagent le même workspace et les mêmes
/// outils (<see cref="WorkspaceRoot"/>) ; Alveus-Evaluator a son propre workspace isolé
/// (<see cref="EvaluatorWorkspaceRoot"/>), cf. ADR 0021.
/// </summary>
public sealed class AlveusTaskWorkflowFixture : IAsyncLifetime
{
    private const string WorkerAgentName = "AlveusWorker";
    private const string EnvironmentManagerAgentName = "AlveusEnvironmentManager";
    private const string EvaluatorAgentName = "AlveusEvaluator";
    private const string UserDocAgentName = "AlveusUserDoc";
    private const string BusinessAnalystAgentName = "AlveusBusinessAnalyst";
    private const string QaAgentName = "AlveusQa";
    private const string TechnicalAgentName = "AlveusTechnical";

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-tests-").FullName;

    public string EvaluatorWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-evaluator-tests-").FullName;

    public string UserDocWorkspaceRoot { get; } = Directory.CreateTempSubdirectory("alveus-workflow-task-userdoc-tests-").FullName;

    public IServiceProvider Services { get; }

    public bool IsLlamaCppAvailable { get; private set; }

    private static Uri Endpoint => new(Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_ENDPOINT") ?? "http://127.0.0.1:8083/v1");

    private static string Model => Environment.GetEnvironmentVariable("ALVEUS_TEST_LLAMACPP_MODEL") ?? "qwen2.5-7b-instruct";

    public AlveusTaskWorkflowFixture()
    {
        EvaluatorSkills.CopyInto(EvaluatorWorkspaceRoot, AppContext.BaseDirectory);

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

        var openAiClient = new OpenAIClient(new ApiKeyCredential("not-needed"), new OpenAIClientOptions
        {
            Endpoint = Endpoint,
        });

        IChatClient chatClient = openAiClient.GetChatClient(Model).AsIChatClient();

        // Worker + EnvironmentManager : même workspace, mêmes outils (ADR 0023).
        services.AddSingleton(_ => new CmdRunTool(WorkspaceRoot));
        services.AddSingleton(_ => new StrReplaceEditorTool(WorkspaceRoot));
        services.AddSingleton<FinishTool>();
        services.AddSingleton<MeetingTool>();
        services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();
        services.AddSingleton<IAgentWorkVerificationService>(_ => new CmdAgentWorkVerificationService(WorkspaceRoot, command: null));

        services.AddKeyedSingleton<AIAgent>(WorkerAgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredService<CmdRunTool>();
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredService<StrReplaceEditorTool>(),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-Worker");
            var finishTool = sp.GetRequiredService<FinishTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.Execute),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-Worker, l'agent d'exécution technique de Butlr. Réponds de façon concise. "
                    + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
                    + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.",
                name: WorkerAgentName,
                tools: tools);
        });

        services.AddKeyedSingleton<AIAgent>(EnvironmentManagerAgentName, (sp, _) =>
        {
            var cmdRunTool = sp.GetRequiredService<CmdRunTool>();
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredService<StrReplaceEditorTool>(),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                "Alveus-EnvironmentManager");
            var finishTool = sp.GetRequiredService<FinishTool>();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(cmdRunTool.RunAsync),
                AIFunctionFactory.Create(editorTool.Execute),
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
                    + "Quand tu arrêtes de travailler, appelle l'outil Finish avec outcome='done' et : verdict='pass' "
                    + "si l'environnement est démarré — résume alors dans summary des instructions d'utilisation "
                    + "précises (URL, ports, exemples de requêtes ou de commandes) destinées à un autre agent qui "
                    + "n'a pas accès à ce système de fichiers ; verdict='fail' si le démarrage échoue (reason=détail "
                    + "de l'échec) ; verdict='needmoreinfo' si la consigne ne précise pas comment démarrer "
                    + "l'environnement (reason et questions).",
                name: EnvironmentManagerAgentName,
                tools: tools);
        });

        // Evaluator : workspace isolé (ADR 0021).
        services.AddKeyedSingleton<CmdRunTool>(EvaluatorAgentName, (_, _) => new CmdRunTool(EvaluatorWorkspaceRoot));
        services.AddKeyedSingleton<StrReplaceEditorTool>(EvaluatorAgentName, (_, _) => new StrReplaceEditorTool(EvaluatorWorkspaceRoot));

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
                AIFunctionFactory.Create(editorTool.Execute),
                AIFunctionFactory.Create(finishTool.Finish),
            };

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
                        + "par les instructions d'utilisation répond à la consigne, en l'écrivant avec ton outil d'édition "
                        + "de fichiers dans ton espace de travail ; puis exécute ce jeu de test avec ton outil shell en "
                        + "interagissant avec l'environnement uniquement par le réseau (ex. curl) — tu n'as pas accès au "
                        + "système de fichiers du Worker. N'effectue pas la tâche toi-même. Des méthodologies de référence "
                        + "pertinentes pour cette tâche te sont fournies directement dans ce contexte ; pour aller plus "
                        + "loin, le dossier 'skills/{nom}/references/' de ton espace de travail contient des fichiers "
                        + "détaillés consultables avec ton outil d'édition. Si le jeu de test repose sur le pattern "
                        + "snapshot/approval testing (skill dotnet-snapshot-testing) : (1) écris un test C# complet (Verify "
                        + "et/ou Playwright selon le besoin) ; (2) lance 'dotnet test' avec ton outil shell — le premier run "
                        + "produit des fichiers non commités ('*.received.json' pour Verify, capture '-actual.png' ou "
                        + "équivalent pour Playwright) ; (3) relis le contenu de ces fichiers avec ton outil d'édition et "
                        + "vérifie manuellement qu'il correspond au résultat attendu pour la consigne ; (4) si c'est "
                        + "correct, renomme ce fichier pour qu'il devienne le golden file de référence ('*.verified.json', "
                        + "ou l'équivalent Playwright — voir le skill). Si le résultat ne correspond pas à la consigne, "
                        + "corrige le test plutôt que de promouvoir un golden file incorrect. Quand tu arrêtes de "
                        + "travailler, tu DOIS appeler l'outil Finish avec outcome='done' et : verdict='pass' si le jeu de "
                        + "test confirme que l'environnement répond à la consigne ; verdict='fail' si ce n'est pas le cas "
                        + "(reason=rapport détaillé des problèmes rencontrés, transmis à Alveus-Worker pour correction) ; "
                        + "verdict='needmoreinfo' si tu ne peux pas trancher sans information supplémentaire (reason et "
                        + "questions). Si tu es bloqué avant d'avoir pu écrire ou exécuter le jeu de test, utilise "
                        + "outcome='blocked' (reason) — sinon on te redemandera de le faire.",
                    Tools = tools,
                },
                AIContextProviders = [new EvaluatorSkillsContextProvider(EvaluatorWorkspaceRoot)],
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
                AIFunctionFactory.Create(editorTool.Execute),
                AIFunctionFactory.Create(finishTool.Finish),
            };

            return new ChatClientAgent(
                chatClient,
                instructions: "Tu es Alveus-UserDoc, l'agent de documentation utilisateur de Butlr. Tu interviens "
                    + "après qu'Alveus-Evaluator a validé le travail d'Alveus-Worker. Ton rôle : mettre à jour la "
                    + "documentation utilisateur (markdown, à la racine de ton espace de travail) pour refléter ce "
                    + "qui change pour l'utilisateur final. Quand tu as terminé, appelle l'outil Finish avec "
                    + "outcome='done' (summary = ce qui a été documenté) ou outcome='needsmoreinfo'/'blocked' si tu "
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
        var technicalWorkspaceRoot = Path.Combine(WorkspaceRoot, "tech-docs");
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
                AIFunctionFactory.Create(editorTool.Execute),
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
        Services.GetRequiredKeyedService<CmdRunTool>(EvaluatorAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(UserDocAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(BusinessAnalystAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(QaAgentName).Dispose();
        Services.GetRequiredKeyedService<CmdRunTool>(TechnicalAgentName).Dispose();
        Directory.Delete(WorkspaceRoot, recursive: true);
        Directory.Delete(EvaluatorWorkspaceRoot, recursive: true);
        Directory.Delete(UserDocWorkspaceRoot, recursive: true);
        return Task.CompletedTask;
    }
}
