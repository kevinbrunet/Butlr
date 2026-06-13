using System.ClientModel;
using Alveus.Web.Activities;
using Alveus.Web.Agents;
using Alveus.Web.Tools;
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
        management.AddActivity<RunEvaluatorPrompt>();
    });
    elsa.UseWorkflowRuntime();
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

builder.Services.AddSingleton(_ => new CmdRunTool(workspaceRoot));
builder.Services.AddSingleton(_ => new StrReplaceEditorTool(workspaceRoot));
builder.Services.AddSingleton<FinishTool>();

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
        instructions: "Tu es Alveus-Worker, l'agent d'exécution technique de Butlr. Réponds de façon concise. "
            + "Quand tu arrêtes de travailler (tâche terminée, besoin de précisions, ou bloqué), tu DOIS appeler "
            + "l'outil Finish pour le signaler — sinon on te redemandera de le faire.",
        name: agentName,
        tools: tools);
});

// Enregistrement non-keyed pour les endpoints qui n'ont besoin que de l'agent par défaut.
builder.Services.AddSingleton(sp => sp.GetRequiredKeyedService<AIAgent>(agentName));

// Agent évaluateur : reçoit le même prompt de tâche que l'agent d'exécution, mais dans un
// workspace isolé — cf. ADR 0021. Son rôle est d'écrire un jeu de test à partir de la consigne,
// pas d'effectuer la tâche.
var evaluatorAgentName = builder.Configuration["Agent:EvaluatorName"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:EvaluatorName");
var evaluatorWorkspaceRootSetting = builder.Configuration["Agent:EvaluatorWorkspaceRoot"]
    ?? throw new InvalidOperationException("Configuration manquante : Agent:EvaluatorWorkspaceRoot");
var evaluatorWorkspaceRoot = Path.GetFullPath(evaluatorWorkspaceRootSetting, builder.Environment.ContentRootPath);
Directory.CreateDirectory(evaluatorWorkspaceRoot);

builder.Services.AddKeyedSingleton<CmdRunTool>(evaluatorAgentName, (_, _) => new CmdRunTool(evaluatorWorkspaceRoot));
builder.Services.AddKeyedSingleton<StrReplaceEditorTool>(evaluatorAgentName, (_, _) => new StrReplaceEditorTool(evaluatorWorkspaceRoot));

// Met à disposition de l'évaluateur les skills méthodologiques du repo (ex. snapshot testing
// .NET) dans son workspace — cf. ADR 0021.
EvaluatorSkills.CopyInto(evaluatorWorkspaceRoot, builder.Environment.ContentRootPath);

builder.Services.AddKeyedSingleton<AIAgent>(evaluatorAgentName, (sp, key) =>
{
    var cmdRunTool = sp.GetRequiredKeyedService<CmdRunTool>(key);
    var editorTool = sp.GetRequiredKeyedService<StrReplaceEditorTool>(key);
    var finishTool = sp.GetRequiredService<FinishTool>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(cmdRunTool.RunAsync),
        AIFunctionFactory.Create(editorTool.Execute),
        AIFunctionFactory.Create(finishTool.Finish),
    };

    return new ChatClientAgent(
        chatClient,
        instructions: "Tu es Alveus-Evaluator, l'agent de validation de Butlr. Tu reçois la même consigne de "
            + "tâche que l'agent d'exécution (Alveus-Worker), mais dans ton propre espace de travail, séparé du "
            + "sien. Ton rôle : à partir de cette consigne, écris un jeu de test (scripts, assertions) qui "
            + "permettrait de vérifier objectivement qu'un travail répondant à la consigne est correct, en "
            + "l'écrivant avec ton outil d'édition de fichiers dans ton espace de travail. N'effectue pas la "
            + "tâche toi-même. Ton espace de travail contient un dossier 'skills/' avec des méthodologies de "
            + "référence (par ex. skills/dotnet-snapshot-testing/SKILL.md pour les tests de non-régression .NET "
            + "par snapshot/approval testing) : consulte-les si la consigne s'y prête. Quand tu arrêtes de "
            + "travailler (jeu de test écrit, besoin de précisions, ou bloqué), tu DOIS appeler l'outil Finish "
            + "pour le signaler — sinon on te redemandera de le faire.",
        name: evaluatorAgentName,
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
