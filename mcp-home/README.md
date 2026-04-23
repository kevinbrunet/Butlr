# mcp-home

Serveur MCP .NET 10 qui exposera les outils de pilotage de la maison à Carlson.

Au POC le seul backend implémenté est `ConsoleMockBackend` : les actions sont loguées dans la console et l'état des lumières est tenu en mémoire. Aucun équipement physique n'est touché.

Contexte et décisions : voir `../docs/architecture.md` et les ADR 0003 / 0005.

## Structure

```
mcp-home/
├── McpHome.sln
├── src/Butlr.McpHome/
│   ├── Butlr.McpHome.csproj     # SDK ASP.NET Core Web
│   ├── Program.cs
│   ├── Backends/
│   │   ├── IDeviceBackend.cs
│   │   └── ConsoleMockBackend.cs
│   ├── appsettings.json
│   └── appsettings.Development.json
├── tests/Butlr.McpHome.Tests/
│   ├── Butlr.McpHome.Tests.csproj
│   └── ConsoleMockBackendTests.cs
└── config/
    └── rooms.example.yaml
```

## Lancer

```bash
dotnet run --project src/Butlr.McpHome
# par défaut : http://0.0.0.0:5090
```

Endpoints de la coquille :

```bash
curl http://localhost:5090/healthz
# {"status":"ok"}

curl http://localhost:5090/state
# {}  (vide tant qu'aucune action n'a été faite)
```

Le transport MCP (SSE/HTTP sur `/mcp`) n'est pas encore câblé. Prochaine étape : §12 étape 2 de l'archi.

## Tester

```bash
dotnet test
```

## État courant

- ✓ Coquille ASP.NET Core + Generic Host + Kestrel
- ✓ `IDeviceBackend` + `ConsoleMockBackend` (thread-safe, case-insensitive sur les noms de pièces)
- ✓ Tests xUnit du backend
- ⬜ Wiring MCP SDK via `ModelContextProtocol.AspNetCore` — étape 2
- ⬜ Auth bearer token (`MCP_HOME_TOKEN`) — posée avec le wiring MCP
- ⬜ Déploiement en service systemd / Windows Service — Phase 2

## Versions

~ `.NET 10` (TFM `net10.0`). Les versions des packages xUnit (`2.9.2`), `Microsoft.NET.Test.Sdk` (`17.11.1`) sont à confirmer au premier `dotnet restore` — n'hésite pas à `dotnet outdated` et remonter.
