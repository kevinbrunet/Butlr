# carlson

Majordome vocal local. Pipecat orchestre : wake word → VAD → STT → LLM (avec tools MCP) → TTS.

## Prérequis

- GPU NVIDIA avec pilotes CUDA à jour.
- Un serveur LLM OpenAI-compatible local — **llama.cpp server** avec Qwen 2.5 7B Instruct GGUF (cf. `docs/architecture.md` §3.3, ADR 0006).
- `mcp-home` démarré et joignable en SSE/HTTP (cf. ADR 0003) — par défaut sur `http://localhost:5090/mcp`.

## Démarrage

```bash
cd carlson
python -m venv .venv && source .venv/bin/activate
pip install -e .[all,dev]
cp .env.example .env   # éditer les variables
carlson
```

## Configuration

Variables d'environnement principales :

| Var | Défaut | Notes |
|---|---|---|
| `LLM_BASE_URL` | `http://localhost:8080/v1` | llama.cpp server (OpenAI-compatible) |
| `LLM_MODEL` | `Qwen/Qwen2.5-7B-Instruct` | libellé affiché, llama.cpp sert le GGUF passé en `-m` |
| `STT_MODEL` | `large-v3` | faster-whisper |
| `TTS_ENGINE` | `piper` | `piper` ou `xtts` |
| `TTS_VOICE_FR` | `fr_FR-siwis-medium` | voix Piper FR par défaut |
| `TTS_VOICE_EN` | `en_US-amy-medium` | voix Piper EN par défaut |
| `WAKEWORD_MODEL` | `assets/wakeword/hey_carlson.tflite` | chemin du modèle custom |
| `WAKEWORD_THRESHOLD` | `0.5` | à ajuster après mesure |
| `MCP_HOME_URL` | `http://localhost:5090/mcp` | endpoint SSE de mcp-home (cf. ADR 0003) |
| `MCP_HOME_TOKEN` | *(vide)* | bearer token partagé ; vide = pas d'auth, OK uniquement sur LAN de dev |
| `FILLER_DELAY_MS` | `500` | seuil du sidecar filler |
| `LANGUAGE_DEFAULT` | `fr` | langue de fallback |

## Entraîner le wake word "Hey Carlson"

Voir `docs/wake-word-training.md` (à venir). openWakeWord génère des données synthétiques via Piper TTS, ~ 30 min à 2 h selon la machine.
