using Butlr.McpHome.Backends;
using Butlr.McpHome.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDeviceBackend, ConsoleMockBackend>();

// MCP server — transport HTTP/SSE sur /mcp (cf. ADR 0003).
// Au POC : pas d'auth bearer (MCP_HOME_TOKEN à câbler en étape suivante).
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<LightTools>();

// Port par défaut 5090 (cf. ADR 0003) si rien n'est explicitement configuré
// via ASPNETCORE_URLS, Kestrel:Endpoints, ou --urls.
var urlsFromEnv = builder.Configuration["ASPNETCORE_URLS"]
                  ?? builder.Configuration["urls"];
var kestrelConfigured = builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
if (string.IsNullOrWhiteSpace(urlsFromEnv) && !kestrelConfigured)
{
    builder.WebHost.UseUrls("http://0.0.0.0:5090");
}

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// /state — dump de l'état mémoire des lumières. Sert les tests manuels et
// la future web UI (docs/architecture.md §7.4). Auth bearer sera ajoutée en
// même temps que MCP_HOME_TOKEN (ADR 0003).
app.MapGet("/state", (IDeviceBackend backend) => Results.Ok(backend.GetLightStates()));

// Endpoint MCP SSE/HTTP (cf. ADR 0003)
app.MapMcp("/mcp");

app.Logger.LogInformation("mcp-home démarre — MCP câblé sur /mcp, backend: ConsoleMockBackend.");

app.Run();
