using Butlr.McpHome.Backends;

var builder = WebApplication.CreateBuilder(args);

// DI — backend des devices. Au POC on n'a que le mock console.
// Le jour où on branche un vrai backend (MQTT, HA), on sélectionnera via config
// (Home:Backend = "console" | "mqtt" | ...).
builder.Services.AddSingleton<IDeviceBackend, ConsoleMockBackend>();

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

// Healthz — preuve de vie du service sans toucher au backend.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// /state — dump de l'état mémoire des lumières. Sert les tests manuels et
// la future web UI (docs/architecture.md §7.4). Pas d'info sensible au POC :
// on laisse public, on ajoutera l'auth bearer en même temps que le transport
// MCP (ADR 0003).
app.MapGet("/state", (IDeviceBackend backend) => Results.Ok(backend.GetLightStates()));

app.Logger.LogInformation(
    "mcp-home démarre — coquille POC (transport MCP non encore câblé, cf. architecture.md §12 étape 2).");

app.Run();
