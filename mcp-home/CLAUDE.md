# CLAUDE.md — mcp-home

Contexte local quand tu travailles dans `mcp-home/`. Le `CLAUDE.md` racine est aussi chargé — celui-ci ne le répète pas.

## Rôle

Serveur MCP long-running. Expose à Carlson les outils de pilotage de la maison via **SSE/HTTP** sur `/mcp` (cf. ADR 0003). Authentification par bearer token partagé (`MCP_HOME_TOKEN`).

Au POC : un seul backend implémenté, `ConsoleMockBackend` — les actions sont loguées, l'état des lumières est tenu en mémoire. Aucun équipement physique touché.

## Structure

```
mcp-home/
├── McpHome.sln
├── src/Butlr.McpHome/
│   ├── Butlr.McpHome.csproj   # SDK Microsoft.NET.Sdk.Web
│   ├── Program.cs             # Minimal API, /healthz, /state
│   ├── Backends/
│   │   ├── IDeviceBackend.cs
│   │   └── ConsoleMockBackend.cs
│   ├── appsettings.json
│   └── appsettings.Development.json
├── tests/Butlr.McpHome.Tests/ # xUnit
└── config/rooms.example.yaml
```

## Conventions .NET

- Namespace racine : `Butlr.McpHome`.
- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Cible unique : **net10.0** (LTS).
- Pas de `Newtonsoft.Json` — `System.Text.Json` uniquement.
- DI : `builder.Services.AddSingleton<IDeviceBackend, ConsoleMockBackend>()` dans `Program.cs`.
- Logs : `ILogger<T>` injecté, structured logging (`log.LogInformation("Light {Room} -> {State}", room, state)`).

## Tests

```bash
dotnet test
```

- Framework : xUnit.
- Un test = un comportement observable (pas un test par méthode).
- Pas de mock de `IDeviceBackend` dans les tests d'intégration MCP quand ils arriveront — on teste contre `ConsoleMockBackend` réel.

## Prochaine étape

Étape 2 de `docs/architecture.md` §12 : **câbler le SDK MCP C# `ModelContextProtocol.AspNetCore`** pour exposer les outils `turn_on_light(room)` / `turn_off_light(room)` / `get_light_states()` sur l'endpoint SSE `/mcp`.

~ Version exacte du SDK à valider au moment du pin.
