# CLAUDE.md — carlson

Contexte local quand tu travailles dans `carlson/`. Le `CLAUDE.md` racine est aussi chargé — celui-ci ne le répète pas.

## Rôle

Majordome vocal local. Pipecat orchestre un pipeline de frames audio → texte → LLM (+ tools MCP) → audio.

Client MCP pur : Carlson n'implémente **aucun** outil de domotique. Il consomme ceux exposés par `mcp-home` via SSE/HTTP.

## Structure

```
carlson/
├── pyproject.toml             # hatchling, extras [stt, vad, wake, tts-piper, tts-xtts, llm, dev, all]
├── src/carlson/
│   ├── main.py                # entrée `carlson`
│   ├── config.py              # Config.from_env()
│   ├── mcp_client.py          # McpHomeClient (SSE)
│   ├── pipeline.py            # build_pipeline(config, mcp)
│   ├── filler.py              # sidecar latence perçue (cf. ADR 0004)
│   ├── persona.py             # system prompt / persona Carlson
│   └── services/
│       ├── llm_local.py       # OpenAILLMService vers llama.cpp server
│       ├── stt_whisper.py     # faster-whisper
│       ├── tts_piper.py       # Piper (TTS par défaut)
│       └── wake_word.py       # openWakeWord
├── tests/test_filler.py
└── assets/wakeword/           # modèle "Hey Carlson" (à entraîner)
```

## Conventions Python

- Python 3.11+ (union types `X | Y`, `from __future__ import annotations` partout).
- Type hints sur tout ce qui est public.
- `ruff` pour lint/format (`line-length = 100`, `target-version = "py311"`).
- Classes `Config` / DTOs = `pydantic` v2 ou `dataclass(frozen=True)` — cohérence dans un même module.
- **Pas d'async partout sans raison** — Pipecat est async, donc le pipeline l'est ; les helpers purs restent sync.
- Imports triés par ruff (isort-compat). Pas de `from x import *`.

## Variables d'environnement clés

Cf. `README.md` pour la liste complète. Les 4 critiques :

| Var | Défaut | Rôle |
|---|---|---|
| `LLM_BASE_URL` | `http://localhost:8080/v1` | llama.cpp server local |
| `MCP_HOME_URL` | `http://localhost:5090/mcp` | endpoint SSE mcp-home |
| `MCP_HOME_TOKEN` | *(vide)* | bearer token ; vide = LAN dev seulement |
| `WAKEWORD_MODEL` | `assets/wakeword/hey_carlson.tflite` | modèle openWakeWord custom |

## Tests

```bash
pytest
```

Peu de tests au POC : la logique métier est dans les services externes (LLM, STT, TTS). Ce qui se teste utilement = adaptateurs, logique de filler, parsing config. Pas de mock de Pipecat — tests d'intégration du pipeline complet viendront en Phase 4.

## Phase actuelle

Phase 0 terminée (nettoyage scaffold). Phase 1 en cours (setup stack locale via `scripts/` racine).
Phases 2-5 détaillées dans le plan remis précédemment (MCP client réel, LLM, pipeline Pipecat, wake word + end-to-end).

## Points d'attention

- ⚠ `pipecat-ai` bouge vite : **pin la version exacte** dès que le pipeline tourne (v0.0.x pre-1.0, breaking changes fréquents ~).
- ~ Flag `--jinja` côté llama.cpp nécessaire pour que le tool calling soit bien formatté. À confirmer avec la version installée.
- ⚠ TTS XTTS (`coqui-tts`) a une licence non-commerciale restrictive — Piper par défaut, XTTS seulement en opt-in pour tests perso.
