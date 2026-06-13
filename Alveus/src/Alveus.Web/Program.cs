using System.ClientModel;
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
    elsa.UseWorkflowManagement();
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

builder.Services.AddSingleton<AIAgent>(sp =>
{
    var cmdRunTool = sp.GetRequiredService<CmdRunTool>();
    var editorTool = sp.GetRequiredService<StrReplaceEditorTool>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(cmdRunTool.RunAsync),
        AIFunctionFactory.Create(editorTool.Execute),
    };

    return new ChatClientAgent(
        chatClient,
        instructions: "Tu es Butlr, le majordome domotique de Kevin. Réponds de façon concise.",
        name: "Butlr",
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
