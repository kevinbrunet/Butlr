# CLAUDE.md — Butlr

Ce fichier est chargé automatiquement par Claude Code au démarrage. Il donne le contexte minimal pour collaborer efficacement sur ce repo. Garde-le à jour quand l'architecture change.

## Projet

Butlr = majordome domotique local. Deux sous-projets :

- **`mcp-home/`** — serveur MCP (.NET 10 / ASP.NET Core) qui expose les outils de pilotage de la maison. Au POC, seul backend = `ConsoleMockBackend` (lumières en mémoire, logs console).
- **`carlson/`** — majordome vocal (Python / Pipecat). Pipeline : wake word → VAD → STT → LLM (+ tool calling MCP) → TTS. Client MCP qui parle à `mcp-home` via SSE/HTTP.

**Stade actuel : POC.** Architecture documentée, scaffolds posés, LLM et wake word pas encore câblés.

## Documentation de référence (à lire avant de toucher à l'archi)

- `docs/architecture.md` — vue complète : topologie, pipeline Pipecat, trade-offs, plan d'implémentation en 10 étapes (§12).
- `docs/adr/` — Architecture Decision Records, un par décision non-triviale :
  - `0001` — monorepo polyglotte
  - `0002` — Pipecat pour l'orchestration
  - `0003` — MCP en SSE/HTTP (service long-running)
  - `0004` — filler sidecar pattern (latence perçue)
  - `0005` — mcp-home en .NET 10
  - `0006` — LLM servi par llama.cpp server + GGUF (Qwen 2.5 7B)

Si une décision change, **mets à jour l'ADR** (ou crée-en un nouveau). Ne modifie pas les faits d'architecture sans tracer la décision.

## Stack

| Zone | Tech | Notes |
|---|---|---|
| `mcp-home` | .NET 10 LTS, ASP.NET Core, Kestrel | Namespace `Butlr.McpHome`. Port 5090 par défaut. |
| `mcp-home` tests | xUnit | `mcp-home/tests/Butlr.McpHome.Tests/` |
| `carlson` | Python 3.11+, Pipecat, `mcp` SDK, faster-whisper, Piper, openWakeWord | venv + `pip install -e .[all,dev]` |
| LLM runtime | llama.cpp server (OpenAI-compat, CUDA) | Qwen 2.5 7B Instruct GGUF Q5_K_M ; flag `--jinja` pour tool calling |
| Scripts setup | PowerShell 7+ (Windows) | `scripts/` — à lancer depuis *Developer PowerShell for VS 2022* |

## Conventions importantes

Voir `.claude/rules/` pour le détail. Résumé :

- **Marqueurs de confiance (`✓` / `~` / `⚠`) OBLIGATOIRES** devant tout chiffre précis, référence normative, benchmark, ou affirmation technique non-triviale. Cf. `.claude/rules/confidence-markers.md`.
- **Toute décision non-triviale = ADR.** Format dans `.claude/rules/adr-writing.md`.
- **Style code** dans `.claude/rules/code-style.md` (C# / Python / PowerShell).

## Commandes utiles

```bash
# mcp-home
dotnet build mcp-home/McpHome.sln
dotnet test mcp-home/McpHome.sln
dotnet run --project mcp-home/src/Butlr.McpHome  # http://localhost:5090

# carlson
cd carlson
python -m venv .venv && . .venv/bin/activate  # (ou .venv\Scripts\Activate.ps1 sur Win)
pip install -e .[all,dev]
pytest
carlson

# stack locale (Windows, Developer PowerShell for VS 2022)
cd scripts
.\Check-Prereqs.ps1
.\Build-Llama.ps1
.\Get-LlamaModel.ps1
.\Start-LlamaServer.ps1
```

## Ce qu'il NE faut PAS faire

- ⚠ Ne pas ajouter de dépendances cloud (OpenAI, ElevenLabs, Google, etc.) sans créer un ADR qui challenge le principe *local-first*.
- ⚠ Ne pas modifier un ADR existant sans lire la section "Status" (certains sont définitifs, d'autres peuvent être *superseded* par un nouveau).
- ⚠ Ne pas "pinner" les versions de `pyproject.toml` / `csproj` sans avoir validé la stack end-to-end (cf. notes dans `scripts/README.md`).
- ⚠ Ne pas committer de secrets (`MCP_HOME_TOKEN`, clés HF, etc.). Pas d'`.env` en git.

## Partenariat attendu

Kevin préfère être challengé à être accompagné. Si une demande te semble discutable (choix techno, scope, trade-off), **dis-le avant d'exécuter** — quitte à exécuter ensuite si Kevin maintient. Pas de ménagement, pas de flattery.
