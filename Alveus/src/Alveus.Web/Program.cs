using System.ClientModel;
using Alveus.Web.Activities;
using Alveus.Web.Agents;
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
builder.Services.AddSingleton<IConversationStore, ConversationStore>();
builder.Services.AddSingleton<IConversationContextAccessor, ConversationContextAccessor>();
builder.Services.AddNotificationHandler<ConversationTransitionNotificationHandler>();

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

// Tools agentiques (shell + édition de fichiers), restreints à un workspace dédié — cf. ADR 0017.
var workspaceRootSetting = builder.Configuration["Agent:WorkspaceRoot"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:WorkspaceRoot");
var workspaceRoot = Path.GetFullPath(workspaceRootSetting, builder.Environment.ContentRootPath);
Directory.CreateDirectory(workspaceRoot);

builder.Services.AddSingleton(sp => new CmdRunTool(workspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddSingleton(_ => new StrReplaceEditorTool(workspaceRoot));
builder.Services.AddSingleton<FinishTool>();

// Outil de débat/vote des réunions de pré-tâche et finale — cf. ADR 0024.
builder.Services.AddSingleton<MeetingTool>();

// Stratégie de compactage de session injectée dans RunAgentPrompt — cf. ADR 0019.
builder.Services.AddSingleton<IAgentSessionCompactionService, SummarizingAgentSessionCompactionService>();

// Vérification du travail avant l'issue "Done" — cf. ADR 0020. Sans Agent:VerificationCommand
// configuré, la vérification est un no-op qui valide toujours.
builder.Services.AddSingleton<IAgentWorkVerificationService>(
    _ => new CmdAgentWorkVerificationService(workspaceRoot, builder.Configuration["Agent:VerificationCommand"]));

// Nom de l'agent : sert à la fois de Name pour le ChatClientAgent et de clé
// d'enregistrement DI, pour que RunAgentPrompt puisse cibler l'agent par son nom.
var agentName = builder.Configuration["Agent:Name"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:Name");

builder.Services.AddKeyedSingleton<AIAgent>(agentName, (sp, _) =>
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
        name: agentName,
        tools: tools);
});

// Enregistrement non-keyed pour les endpoints qui n'ont besoin que de l'agent par défaut.
builder.Services.AddSingleton(sp => sp.GetRequiredKeyedService<AIAgent>(agentName));

// Agent EnvironmentManager : intervient après le Worker pour lancer/relancer l'environnement
// local, avec les mêmes outils et le même workspace que lui (Agent:WorkspaceRoot) — cf. ADR 0023.
var environmentManagerAgentName = builder.Configuration["Agent:EnvironmentManagerName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:EnvironmentManagerName");

builder.Services.AddKeyedSingleton<AIAgent>(environmentManagerAgentName, (sp, _) =>
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
        instructions: "Tu es Alveus-EnvironmentManager, l'agent de gestion d'environnement de Butlr. Tu interviens "
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
        name: environmentManagerAgentName,
        tools: tools);
});

// Agent évaluateur : reçoit le même prompt de tâche que l'agent d'exécution, mais dans un
// workspace isolé — cf. ADR 0021. Son rôle est d'écrire un jeu de test à partir de la consigne,
// pas d'effectuer la tâche.
var evaluatorAgentName = builder.Configuration["Agent:EvaluatorName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:EvaluatorName");
var evaluatorWorkspaceRootSetting = builder.Configuration["Agent:EvaluatorWorkspaceRoot"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:EvaluatorWorkspaceRoot");
var evaluatorWorkspaceRoot = Path.GetFullPath(evaluatorWorkspaceRootSetting, builder.Environment.ContentRootPath);
Directory.CreateDirectory(evaluatorWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(evaluatorAgentName, (sp, _) => new CmdRunTool(evaluatorWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(evaluatorAgentName, (_, _) => new StrReplaceEditorTool(evaluatorWorkspaceRoot));

// Met à disposition de l'évaluateur les skills méthodologiques du repo (ex. snapshot testing
// .NET) dans son workspace — cf. ADR 0021.
EvaluatorSkills.CopyInto(evaluatorWorkspaceRoot, builder.Environment.ContentRootPath);

builder.Services.AddKeyedSingleton<AIAgent>(evaluatorAgentName, (sp, key) =>
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
        Name = evaluatorAgentName,
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

// Agent UserDoc : intervient après l'Evaluator pour mettre à jour la documentation utilisateur,
// dans son propre espace de travail (Agent:UserDocWorkspaceRoot) — cf. ADR 0026. Le sous-dossier
// 'business-rules/' de cet espace est celui d'Alveus-BusinessAnalyst (cf. ADR 0025).
var userDocAgentName = builder.Configuration["Agent:UserDocName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:UserDocName");
var userDocWorkspaceRootSetting = builder.Configuration["Agent:UserDocWorkspaceRoot"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:UserDocWorkspaceRoot");
var userDocWorkspaceRoot = Path.GetFullPath(userDocWorkspaceRootSetting, builder.Environment.ContentRootPath);
Directory.CreateDirectory(userDocWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(userDocAgentName, (sp, _) => new CmdRunTool(userDocWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(userDocAgentName, (_, _) => new StrReplaceEditorTool(userDocWorkspaceRoot));

builder.Services.AddKeyedSingleton<AIAgent>(userDocAgentName, (sp, key) =>
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
        instructions: "Tu es Alveus-UserDoc, l'agent de documentation utilisateur de Butlr. Tu interviens après "
            + "qu'Alveus-Evaluator a validé le travail d'Alveus-Worker. Ton rôle : mettre à jour la documentation "
            + "utilisateur (markdown, à la racine de ton espace de travail) pour refléter ce qui change pour "
            + "l'utilisateur final, à partir de la consigne de tâche et des instructions complémentaires éventuelles "
            + "d'Alveus-Technical. Le sous-dossier 'business-rules/' appartient à Alveus-BusinessAnalyst : tu peux le "
            + "consulter mais ne le modifie pas. Quand tu as terminé, appelle l'outil Finish avec outcome='done' "
            + "(summary = ce qui a été documenté) ou outcome='needsmoreinfo'/'blocked' si tu ne peux pas avancer.",
        name: userDocAgentName,
        tools: tools);
});

// Agent Alveus-Technical : participe aux réunions de pré-tâche et finale (cf. ADR 0024), espace de
// travail enraciné sur un sous-dossier de celui d'Alveus-Worker (cf. ADR 0025).
var technicalAgentName = builder.Configuration["Agent:TechnicalName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:TechnicalName");
var technicalWorkspaceSubdir = builder.Configuration["Agent:TechnicalWorkspaceSubdir"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:TechnicalWorkspaceSubdir");
var technicalWorkspaceRoot = Path.Combine(workspaceRoot, technicalWorkspaceSubdir);
Directory.CreateDirectory(technicalWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(technicalAgentName, (sp, _) => new CmdRunTool(technicalWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(technicalAgentName, (_, _) => new StrReplaceEditorTool(technicalWorkspaceRoot));

builder.Services.AddKeyedSingleton<AIAgent>(technicalAgentName, (sp, key) =>
{
    var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
    var editorTool = new ConversationAwareStrReplaceEditorTool(
        sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
        sp.GetRequiredService<IConversationContextAccessor>(),
        sp.GetRequiredService<IConversationStore>(),
        "Alveus-Technical");
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
        instructions: "Tu es Alveus-Technical, l'agent d'architecture de Butlr. Ton espace de travail est un "
            + "sous-dossier de celui d'Alveus-Worker ('tech-docs/') où tu maintiens la documentation d'architecture "
            + "et les ADR (cf. conventions '.claude/rules/adr-writing.md'). Tu participes à des réunions à 3 avec "
            + "Alveus-BusinessAnalyst et Alveus-Qa : utilise l'outil Raise pour signaler un point de désaccord ou une "
            + "question aux 2 autres participants, et Vote pour te positionner sur un topic ('agree'/'disagree', "
            + "commentaire obligatoire si 'disagree'). Quand tu as terminé ton tour, appelle l'outil Finish avec "
            + "outcome='done' (et, le cas échéant, downstreamInstructions pour Alveus-Worker et/ou Alveus-UserDoc) ou "
            + "outcome='needsmoreinfo'/'blocked' si tu es bloqué.",
        name: technicalAgentName,
        tools: tools);
});

// Agent Alveus-Qa : participe aux réunions de pré-tâche et finale (cf. ADR 0024), espace de travail
// enraciné sur un sous-dossier de celui d'Alveus-Evaluator (cf. ADR 0025).
var qaAgentName = builder.Configuration["Agent:QaName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:QaName");
var qaWorkspaceSubdir = builder.Configuration["Agent:QaWorkspaceSubdir"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:QaWorkspaceSubdir");
var qaWorkspaceRoot = Path.Combine(evaluatorWorkspaceRoot, qaWorkspaceSubdir);
Directory.CreateDirectory(qaWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(qaAgentName, (sp, _) => new CmdRunTool(qaWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(qaAgentName, (_, _) => new StrReplaceEditorTool(qaWorkspaceRoot));

builder.Services.AddKeyedSingleton<AIAgent>(qaAgentName, (sp, key) =>
{
    var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
    var editorTool = new ConversationAwareStrReplaceEditorTool(
        sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
        sp.GetRequiredService<IConversationContextAccessor>(),
        sp.GetRequiredService<IConversationStore>(),
        "Alveus-Qa");
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
        instructions: "Tu es Alveus-Qa, l'agent de plan de test de Butlr. Ton espace de travail est un sous-dossier "
            + "de celui d'Alveus-Evaluator ('test-plan/') où tu maintiens le plan de test markdown (cas passants et "
            + "non passants). Tu participes à des réunions à 3 avec Alveus-BusinessAnalyst et Alveus-Technical : "
            + "utilise l'outil Raise pour signaler un point de désaccord ou une question aux 2 autres participants, "
            + "et Vote pour te positionner sur un topic ('agree'/'disagree', commentaire obligatoire si 'disagree'). "
            + "Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='done' (et, le cas échéant, "
            + "downstreamInstructions pour Alveus-Evaluator) ou outcome='needsmoreinfo'/'blocked' si tu es bloqué.",
        name: qaAgentName,
        tools: tools);
});

// Agent Alveus-BusinessAnalyst : participe aux réunions de pré-tâche et finale (cf. ADR 0024),
// espace de travail enraciné sur un sous-dossier de celui d'Alveus-UserDoc (cf. ADR 0025).
var businessAnalystAgentName = builder.Configuration["Agent:BusinessAnalystName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:BusinessAnalystName");
var businessAnalystWorkspaceSubdir = builder.Configuration["Agent:BusinessAnalystWorkspaceSubdir"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:BusinessAnalystWorkspaceSubdir");
var businessAnalystWorkspaceRoot = Path.Combine(userDocWorkspaceRoot, businessAnalystWorkspaceSubdir);
Directory.CreateDirectory(businessAnalystWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(businessAnalystAgentName, (sp, _) => new CmdRunTool(businessAnalystWorkspaceRoot, sp.GetRequiredService<ILogger<CmdRunTool>>()));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(businessAnalystAgentName, (_, _) => new StrReplaceEditorTool(businessAnalystWorkspaceRoot));

builder.Services.AddKeyedSingleton<AIAgent>(businessAnalystAgentName, (sp, key) =>
{
    var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
    var editorTool = new ConversationAwareStrReplaceEditorTool(
        sp.GetRequiredKeyedService<StrReplaceEditorTool>(key),
        sp.GetRequiredService<IConversationContextAccessor>(),
        sp.GetRequiredService<IConversationStore>(),
        "Alveus-BusinessAnalyst");
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
        instructions: "Tu es Alveus-BusinessAnalyst, l'agent de règles métier de Butlr. Ton espace de travail est un "
            + "sous-dossier de celui d'Alveus-UserDoc ('business-rules/') où tu maintiens la documentation des règles "
            + "métier en markdown, organisée par domaine. Tu participes à des réunions à 3 avec Alveus-Qa et "
            + "Alveus-Technical : utilise l'outil Raise pour signaler un point de désaccord ou une question aux 2 "
            + "autres participants, et Vote pour te positionner sur un topic ('agree'/'disagree', commentaire "
            + "obligatoire si 'disagree'). Quand tu as terminé ton tour, appelle l'outil Finish avec outcome='done' "
            + "ou outcome='needsmoreinfo'/'blocked' si tu es bloqué.",
        name: businessAnalystAgentName,
        tools: tools);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseWorkflows();
app.UseWorkflowsApi();

app.MapConversationEndpoints();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapPost("/agent/chat", async (AgentChatRequest request, AIAgent agent, CancellationToken cancellationToken) =>
{
    var response = await agent.RunAsync(request.Message, cancellationToken: cancellationToken);
    return new AgentChatResponse(response.Text);
})
.WithName("AgentChat");

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record AgentChatRequest(string Message);

record AgentChatResponse(string Reply);
