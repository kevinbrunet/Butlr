using System.ClientModel;
using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Configuration;
using Alveus.Web.Conversations;
using Alveus.Web.Tools;
using Alveus.Web.Workflows;
using Elsa.Extensions;
using FastEndpoints;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Elsa workflows : gestion + exécution. UseHttp() expose les activités HTTP, UseWorkflowsApi() l'API REST (FastEndpoints).
// UseIdentity()/UseDefaultAuthentication() : requis par UseWorkflowsApi() (pipeline d'autorisation FastEndpoints).
builder.Services.AddElsa(elsa =>
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
    elsa.UseHttp();
    elsa.UseJavaScript();
    elsa.UseWorkflowsApi();
    elsa.UseIdentity(identity =>
    {
        identity.TokenOptions = options => options.SigningKey = builder.Configuration["Elsa:Identity:SigningKey"]
            ?? throw new InvalidOperationException("Configuration manquante : Elsa:Identity:SigningKey");
        identity.UseAdminUserProvider();
    });
    elsa.UseDefaultAuthentication(auth => auth.UseAdminApiKey());
});
builder.Services.AddFastEndpoints();
builder.Services.AddAuthorization();

// API de conversation au format OpenAI (self-hosted, cf. ADR 0027) : point d'entrée et canal
// d'aide humaine/observabilité pour AlveusTaskWorkflow.
builder.Services.AddSingleton<Alveus.Web.Logging.ITaskLogger, Alveus.Web.Logging.FileTaskLogger>();
builder.Services.AddSingleton<IConversationStore, ConversationStore>();
builder.Services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
builder.Services.AddNotificationHandler<ConversationTransitionNotificationHandler>();

// Outils et services partagés entre toutes les équipes.
builder.Services.AddSingleton<FinishTool>();
builder.Services.AddSingleton<MeetingTool>();
builder.Services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();

// Agent IA branché sur llama.cpp server (endpoint OpenAI-compatible local, cf. ADR 0006).
var llamaCppEndpoint = builder.Configuration["LlamaCpp:Endpoint"]
    ?? throw new InvalidOperationException("Configuration manquante : LlamaCpp:Endpoint");
var llamaCppModel = builder.Configuration["LlamaCpp:Model"]
    ?? throw new InvalidOperationException("Configuration manquante : LlamaCpp:Model");

// llama.cpp n'exige pas de clé API mais le SDK OpenAI en réclame une non vide.
var openAiClient = new OpenAIClient(new ApiKeyCredential("not-needed"), new OpenAIClientOptions
{
    Endpoint = new Uri(llamaCppEndpoint),
});

IChatClient chatClient = openAiClient.GetChatClient(llamaCppModel).AsIChatClient();

// Équipes (cf. ADR 0031) : chaque équipe déclare ses workspaces, ses spécialistes actifs et son
// MissionPrompt. Un jeu d'agents DI isolés est enregistré par équipe (clés "{Name}:{role}"),
// et un endpoint de conversation distinct est exposé sous /teams/{Name}/v1/conversations.
var teams = builder.Configuration.GetSection("Teams").Get<TeamConfig[]>()
    ?? throw new InvalidOperationException("Configuration manquante : Teams");

if (teams.Length == 0)
    throw new InvalidOperationException("Au moins une équipe est requise dans Teams.");

foreach (var team in teams)
{
    if (string.IsNullOrWhiteSpace(team.Name))
        throw new InvalidOperationException("Chaque équipe doit avoir un Name non vide.");

    var missionPrefix = string.IsNullOrWhiteSpace(team.MissionPrompt) ? "" : $"{team.MissionPrompt}\n\n---\n";

    var workerWorkspaceRoot = Path.GetFullPath(team.WorkspaceRoot, builder.Environment.ContentRootPath);
    var evaluatorWorkspaceRoot = Path.GetFullPath(team.EvaluatorWorkspaceRoot, builder.Environment.ContentRootPath);
    var userDocWorkspaceRoot = Path.GetFullPath(team.UserDocWorkspaceRoot, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(workerWorkspaceRoot);
    Directory.CreateDirectory(evaluatorWorkspaceRoot);
    Directory.CreateDirectory(userDocWorkspaceRoot);

    // AskExpertTool : toujours enregistré (HTTP endpoint experts), ajouté au Worker si EscalationMode="tool".
    builder.Services.AddKeyedSingleton<AskExpertTool>(team.Name, (sp, _) =>
        new AskExpertTool(
            team.Name,
            sp,
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IConversationContextAccessor>()));

    // Tools Worker/EnvironmentManager (workspace partagé) — clé commune "{team.Name}:Worker".
    builder.Services.AddKeyedSingleton<CmdRunTool>($"{team.Name}:Worker", (sp, _) => new CmdRunTool(workerWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
    builder.Services.AddKeyedSingleton<StrReplaceEditorTool>($"{team.Name}:Worker", (_, _) => new StrReplaceEditorTool(workerWorkspaceRoot));

    // Vérification du travail — cf. ADR 0020.
    builder.Services.AddKeyedSingleton<IAgentWorkVerificationService>(team.Name, (_, _) =>
        new CmdAgentWorkVerificationService(workerWorkspaceRoot, team.VerificationCommand));

    // Agent Worker.
    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:Worker", (sp, key) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:Worker");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:Worker"),
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

        var workerInstructions = missionPrefix + "Tu es Alveus-Worker, l'agent d'exécution technique de l'équipe. Réponds de façon concise. "
            + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
            + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.";

        if (team.EscalationMode == "tool")
        {
            var askExpert = sp.GetRequiredKeyedService<AskExpertTool>(team.Name);
            tools.Add(AIFunctionFactory.Create(askExpert.AskExpertAsync));
            workerInstructions += " Si tu as besoin d'une information métier, UX ou technique pour accomplir la tâche, "
                + "utilise l'outil AskExpert pour interroger directement l'expert concerné plutôt que de terminer "
                + "en 'needsmoreinfo'.";
        }

        return new ChatClientAgent(chatClient, instructions: workerInstructions, name: "Alveus-Worker", tools: tools);
    });

    // Agent EnvironmentManager (même workspace que Worker) — cf. ADR 0023.
    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:EnvironmentManager", (sp, _) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:Worker");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:Worker"),
            sp.GetRequiredService<IConversationContextAccessor>(),
            sp.GetRequiredService<IConversationStore>(),
            "Alveus-EnvironmentManager");
        var finishTool = sp.GetRequiredService<FinishTool>();
        return new ChatClientAgent(
            chatClient,
            instructions: missionPrefix + "Tu es Alveus-EnvironmentManager, l'agent de gestion d'environnement. Tu interviens "
                + "après que l'agent d'exécution (Alveus-Worker) a terminé sa tâche, dans le même espace de travail et "
                + "avec les mêmes outils que lui (mêmes fichiers, même shell). Ton rôle : lancer ou relancer "
                + "l'environnement local décrit par la consigne (ex. démarrer un serveur ou une application) pour qu'il "
                + "soit utilisable par un autre agent. Ton outil shell a un timeout de 30 secondes : lance les "
                + "processus longue durée en arrière-plan (ex. 'nohup <commande> > /tmp/env.log 2>&1 & disown') plutôt "
                + "qu'au premier plan, puis vérifie leur démarrage (ex. en consultant le log ou en testant le port). "
                + "Quand tu arrêtes de travailler, appelle l'outil Finish avec outcome='done' et : verdict='pass' si "
                + "l'environnement est démarré — résume alors dans summary des instructions d'utilisation précises "
                + "(URL, ports, exemples de requêtes ou de commandes) destinées à un autre agent qui n'a pas accès à ce "
                + "système de fichiers ; verdict='fail' si le démarrage échoue (reason=détail de l'échec) ; "
                + "verdict='needmoreinfo' si la consigne ne précise pas comment démarrer l'environnement (reason et "
                + "questions).",
            name: "Alveus-EnvironmentManager",
            tools: [AIFunctionFactory.Create(cmdRunTool.RunAsync), AIFunctionFactory.Create(editorTool.Execute), AIFunctionFactory.Create(finishTool.Finish)]);
    });

    // Agent Evaluator : workspace isolé — cf. ADR 0021.
    builder.Services.AddKeyedSingleton<CmdRunTool>($"{team.Name}:Evaluator", (sp, _) => new CmdRunTool(evaluatorWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
    builder.Services.AddKeyedSingleton<StrReplaceEditorTool>($"{team.Name}:Evaluator", (_, _) => new StrReplaceEditorTool(evaluatorWorkspaceRoot));
    EvaluatorSkills.CopyInto(evaluatorWorkspaceRoot, builder.Environment.ContentRootPath);

    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:Evaluator", (sp, _) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:Evaluator");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:Evaluator"),
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
            Name = "Alveus-Evaluator",
            ChatOptions = new ChatOptions
            {
                Instructions = missionPrefix + "Tu es Alveus-Evaluator, l'agent de validation. Tu reçois la même consigne de "
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
                    + "snapshot/approval testing (skill dotnet-snapshot-testing) : (1) écris un test C# complet — "
                    + "Playwright si la consigne décrit une interface utilisateur (pages web, interactions via "
                    + "navigateur), Verify si elle décrit une API/réponse JSON sans interface ; (2) lance 'dotnet "
                    + "test' avec ton outil shell — le premier run "
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
            AIContextProviders = [new EvaluatorSkillsContextProvider(evaluatorWorkspaceRoot)],
        });
    });

    // Agent UserDoc : workspace dédié — cf. ADR 0026. Les sous-dossiers des spécialistes y sont
    // imbriqués (cf. ADR 0025/0030).
    builder.Services.AddKeyedSingleton<CmdRunTool>($"{team.Name}:UserDoc", (sp, _) => new CmdRunTool(userDocWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
    builder.Services.AddKeyedSingleton<StrReplaceEditorTool>($"{team.Name}:UserDoc", (_, _) => new StrReplaceEditorTool(userDocWorkspaceRoot));

    var specialistSubdirNotes = string.Concat(team.SpecialistRoles.Select(sr =>
    {
        var def = SpecialistRoleCatalog.Roles.TryGetValue(sr.Key, out var found)
            ? found
            : throw new InvalidOperationException($"Équipe '{team.Name}' : SpecialistRoles contient '{sr.Key}', absent de SpecialistRoleCatalog.");
        return $" Le sous-dossier '{def.WorkspaceSubdir}/' appartient à {def.DisplayName} : tu peux le consulter mais ne le modifie pas.";
    }));

    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:UserDoc", (sp, _) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:UserDoc");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:UserDoc"),
            sp.GetRequiredService<IConversationContextAccessor>(),
            sp.GetRequiredService<IConversationStore>(),
            "Alveus-UserDoc");
        var finishTool = sp.GetRequiredService<FinishTool>();
        return new ChatClientAgent(
            chatClient,
            instructions: missionPrefix + "Tu es Alveus-UserDoc, l'agent de documentation utilisateur. Tu interviens après "
                + "qu'Alveus-Evaluator a validé le travail d'Alveus-Worker. Ton rôle : mettre à jour la documentation "
                + "utilisateur (markdown, à la racine de ton espace de travail) pour refléter ce qui change pour "
                + "l'utilisateur final, à partir de la consigne de tâche et des instructions complémentaires éventuelles "
                + $"d'Alveus-Technical.{specialistSubdirNotes} Quand tu as terminé, appelle l'outil Finish avec "
                + "outcome='done' (summary = ce qui a été documenté) ou outcome='needsmoreinfo'/'blocked' si tu ne peux "
                + "pas avancer.",
            name: "Alveus-UserDoc",
            tools: [AIFunctionFactory.Create(cmdRunTool.RunAsync), AIFunctionFactory.Create(editorTool.Execute), AIFunctionFactory.Create(finishTool.Finish)]);
    });

    // Agent Technical : sous-dossier de l'espace Worker — cf. ADR 0025.
    var technicalWorkspaceRoot = Path.Combine(workerWorkspaceRoot, "tech-docs");
    Directory.CreateDirectory(technicalWorkspaceRoot);
    builder.Services.AddKeyedSingleton<CmdRunTool>($"{team.Name}:Technical", (sp, _) => new CmdRunTool(technicalWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
    builder.Services.AddKeyedSingleton<StrReplaceEditorTool>($"{team.Name}:Technical", (_, _) => new StrReplaceEditorTool(technicalWorkspaceRoot));

    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:Technical", (sp, _) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:Technical");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:Technical"),
            sp.GetRequiredService<IConversationContextAccessor>(),
            sp.GetRequiredService<IConversationStore>(),
            "Alveus-Technical");
        var finishTool = sp.GetRequiredService<FinishTool>();
        var meetingTool = sp.GetRequiredService<MeetingTool>();
        return new ChatClientAgent(
            chatClient,
            instructions: missionPrefix + "Tu es Alveus-Technical, l'agent d'architecture. Ton workspace est un dossier "
                + "dédié UNIQUEMENT à la documentation : ADR, conventions, notes d'architecture "
                + "(cf. '.claude/rules/adr-writing.md'). Crée tes fichiers directement à la racine de ton workspace "
                + "(ex. 'adr/0001-foo.md', 'rules/code-style.md'), jamais dans un sous-dossier 'tech-docs/' — tu es "
                + "DÉJÀ à l'intérieur de ce dossier. "
                + "INTERDIT : écrire ou exécuter du code source, créer des projets, compiler, lancer des tests. "
                + "Le code existe dans le workspace d'Alveus-Worker — tu n'y as pas accès et ce n'est pas ton rôle. "
                + "Tu participes à des réunions à plusieurs participants avec Alveus-Qa et les spécialistes configurés : "
                + "utilise l'outil Raise pour signaler un point de désaccord ou une question aux autres participants, "
                + "et Vote pour te positionner sur un topic ('agree'/'disagree', commentaire obligatoire si 'disagree'). "
                + "Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='done' (et, le cas échéant, "
                + "downstreamInstructions pour Alveus-Worker et/ou Alveus-UserDoc) ou outcome='needsmoreinfo'/'blocked' "
                + "si tu es bloqué.",
            name: "Alveus-Technical",
            tools: [AIFunctionFactory.Create(cmdRunTool.RunAsync), AIFunctionFactory.Create(editorTool.Execute), AIFunctionFactory.Create(finishTool.Finish), AIFunctionFactory.Create(meetingTool.Raise), AIFunctionFactory.Create(meetingTool.Vote)]);
    });

    // Agent Qa : sous-dossier de l'espace Evaluator — cf. ADR 0025.
    var qaWorkspaceRoot = Path.Combine(evaluatorWorkspaceRoot, "test-plan");
    Directory.CreateDirectory(qaWorkspaceRoot);
    builder.Services.AddKeyedSingleton<CmdRunTool>($"{team.Name}:Qa", (sp, _) => new CmdRunTool(qaWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
    builder.Services.AddKeyedSingleton<StrReplaceEditorTool>($"{team.Name}:Qa", (_, _) => new StrReplaceEditorTool(qaWorkspaceRoot));

    builder.Services.AddKeyedSingleton<AIAgent>($"{team.Name}:Qa", (sp, _) =>
    {
        var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>($"{team.Name}:Qa");
        var editorTool = new ConversationAwareStrReplaceEditorTool(
            sp.GetRequiredKeyedService<StrReplaceEditorTool>($"{team.Name}:Qa"),
            sp.GetRequiredService<IConversationContextAccessor>(),
            sp.GetRequiredService<IConversationStore>(),
            "Alveus-Qa");
        var finishTool = sp.GetRequiredService<FinishTool>();
        var meetingTool = sp.GetRequiredService<MeetingTool>();
        return new ChatClientAgent(
            chatClient,
            instructions: missionPrefix + "Tu es Alveus-Qa, l'agent de plan de test. Ton workspace est un dossier dédié "
                + "UNIQUEMENT à la documentation du plan de test : cas passants, cas non-passants, critères "
                + "d'acceptance en markdown. Crée tes fichiers directement à la racine de ton workspace "
                + "(ex. 'test-plan.md'), jamais dans un sous-dossier 'test-plan/' — tu es DÉJÀ à l'intérieur de "
                + "ce dossier. "
                + "INTERDIT : exécuter des tests, compiler du code, lancer des scripts, vérifier si des tests passent. "
                + "L'exécution des tests est le rôle d'Alveus-Evaluator qui a accès au code — toi non. "
                + "Tu participes à des réunions à plusieurs participants avec Alveus-Technical et les "
                + "spécialistes configurés : utilise l'outil Raise pour signaler un point de désaccord ou une question "
                + "aux autres participants, et Vote pour te positionner sur un topic ('agree'/'disagree', commentaire "
                + "obligatoire si 'disagree'). Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='done' "
                + "(et, le cas échéant, downstreamInstructions pour Alveus-Evaluator) ou outcome='needsmoreinfo'/'blocked' si tu es bloqué.",
            name: "Alveus-Qa",
            tools: [AIFunctionFactory.Create(cmdRunTool.RunAsync), AIFunctionFactory.Create(editorTool.Execute), AIFunctionFactory.Create(finishTool.Finish), AIFunctionFactory.Create(meetingTool.Raise), AIFunctionFactory.Create(meetingTool.Vote)]);
    });

    // Agents spécialistes (cf. ADR 0024/0025/0030) : catalogue C#, activés par équipe via SpecialistRoles.
    foreach (var sr in team.SpecialistRoles)
    {
        var def = SpecialistRoleCatalog.Roles[sr.Key];
        var specialistWorkspaceRoot = Path.Combine(userDocWorkspaceRoot, def.WorkspaceSubdir);
        Directory.CreateDirectory(specialistWorkspaceRoot);

        var specialistKey = $"{team.Name}:{sr.Key}";
        var additionalInstructions = string.IsNullOrWhiteSpace(sr.AdditionalInstructions)
            ? ""
            : $"\n\n---\n{sr.AdditionalInstructions}";

        builder.Services.AddKeyedSingleton<CmdRunTool>(specialistKey, (sp, _) => new CmdRunTool(specialistWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
        builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(specialistKey, (_, _) => new StrReplaceEditorTool(specialistWorkspaceRoot));

        builder.Services.AddKeyedSingleton<AIAgent>(specialistKey, (sp, _) =>
        {
            var capturedKey = specialistKey;
            var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(capturedKey);
            var editorTool = new ConversationAwareStrReplaceEditorTool(
                sp.GetRequiredKeyedService<StrReplaceEditorTool>(capturedKey),
                sp.GetRequiredService<IConversationContextAccessor>(),
                sp.GetRequiredService<IConversationStore>(),
                def.DisplayName);
            var finishTool = sp.GetRequiredService<FinishTool>();
            var meetingTool = sp.GetRequiredService<MeetingTool>();
            return new ChatClientAgent(
                chatClient,
                instructions: missionPrefix + def.SystemInstructions + additionalInstructions,
                name: def.DisplayName,
                tools: [AIFunctionFactory.Create(cmdRunTool.RunAsync), AIFunctionFactory.Create(editorTool.Execute), AIFunctionFactory.Create(finishTool.Finish), AIFunctionFactory.Create(meetingTool.Raise), AIFunctionFactory.Create(meetingTool.Vote)]);
        });
    }
}

var app = builder.Build();

// Les factories d'agents capturent `chatClient` par référence (closure C#) — on réassigne ici,
// après Build(), avant que les singletons soient résolus. Toutes les factories verront les wrappers.
//
// Chaîne finale (du plus bas au plus haut) :
//   raw LLM → LoggingChatClient → FunctionInvokingChatClient → ChatClientAgent
//
// LoggingChatClient intercepte chaque appel LLM individuel (avant tool-calling).
// FunctionInvokingChatClient gère la boucle d'exécution des outils avec un plafond de 20 rounds
// pour éviter les boucles infinies (ex. LLM qui re-appelle Finish indéfiniment).
// ChatClientAgent détecte FunctionInvokingChatClient dans la chaîne et n'en rajoute pas un second.
var loggingClient = new Alveus.Web.Logging.LoggingChatClient(
    chatClient,
    app.Services.GetRequiredService<Alveus.Web.Logging.ITaskLogger>(),
    app.Services.GetRequiredService<IConversationContextAccessor>(),
    app.Services.GetRequiredService<IConversationStore>());

chatClient = new FunctionInvokingChatClient(loggingClient)
{
    MaximumIterationsPerRequest = 20,
};

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();

app.UseWorkflows();
app.UseWorkflowsApi();

app.MapConversationEndpoints(teams.Select(t => t.Name));
app.MapChatCompletionsEndpoints(teams.Select(t => t.Name));
app.MapWebSocketEndpoints(teams.Select(t => t.Name));
app.MapExpertEndpoints(teams.Select(t => t.Name));

app.Run();
