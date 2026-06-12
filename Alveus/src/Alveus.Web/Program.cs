using Elsa.Extensions;
using FastEndpoints;

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

// TODO(Phase Alveus): câbler Microsoft.Agents.AI avec un IChatClient.
// Pas de provider configuré ici — voir CLAUDE.md (pas de dépendance cloud sans ADR).
// Si l'agent doit parler à llama.cpp (endpoint OpenAI-compatible local), ajouter
// Microsoft.Extensions.AI.OpenAI et pointer le client sur l'URL locale via config.

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
