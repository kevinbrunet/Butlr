# scripts — setup Phase 1 (Bash / Fedora)

Outillage pour monter la stack locale de Carson : faster-whisper (STT), Piper TTS, wake word.

Le LLM (Qwen 3) tourne sur un serveur distant du LAN (`LLM_BASE_URL` dans `env.sh`).

## Prérequis

- Fedora Workstation (ou toute distro Linux récente)
- GPU NVIDIA, drivers récents (`sudo dnf install akmod-nvidia` via rpmfusion-nonfree) — pour faster-whisper CUDA
- `git`, `python3`, `curl`, `jq` — tous installables via `sudo dnf install`
- Docker (pour `train-wakeword.sh`) — voir https://docs.docker.com/engine/install/fedora/

## Configuration

```bash
cd scripts
cp env.example.sh env.sh
# Édite env.sh si tu veux changer BUTLR_ENV_DIR, LLM_BASE_URL, la voix, etc.
```

Par défaut, tout atterrit dans `~/butlr-env/` (modèles, voix) — hors du repo pour éviter les gigas en git.

## Pipeline

```bash
# 1. Sanity check : GPU, outillage, joignabilité du serveur LLM.
./check-prereqs.sh

# 2. Smoke test du serveur LLM distant.
./test-llama-server.sh
```

### Whisper (STT)

```bash
cd carson
python3 -m venv .venv && . .venv/bin/activate
pip install -e '.[all]'
# Télécharge le modèle faster-whisper large-v3 (~3 GB).
cd ../scripts && ./get-whisper-model.sh
# Lance sur le WAV de test fourni (LJSpeech, anglais, domaine public, 5 s, 16 kHz mono).
python3 Test-Whisper.py testdata/sample.wav
```

### Piper (TTS)

```bash
cd carson && . .venv/bin/activate
pip install piper-tts

cd ../scripts
./get-piper-voices.sh     # download FR + EN
./test-piper.sh           # synthèse de samples
```

### Wake word (Phase 4)

```bash
# Génère les clips d'entraînement (200 clips par voix Piper).
./generate-french-tts.sh

# Entraîne le modèle via Docker (CPU ~2-4 h, GPU ~45 min).
./train-wakeword.sh
./train-wakeword.sh --gpu    # si NVIDIA Container Toolkit configuré
```

## Conventions

- Nommage kebab-case minuscule (`get-whisper-model.sh`, etc.).
- Tous les scripts sourcent `_lib.sh` puis appellent `import_btlr_env`.
- `curl` avec `--fail` pour les downloads — exit != 0 sur HTTP 4xx/5xx.
- `set -euo pipefail` partout.

## Notes

- Le LLM distant doit exposer une API OpenAI-compatible (`/v1/models`, `/v1/chat/completions`).
- Les versions (faster-whisper, piper-tts) ne sont **pas épinglées** ici — à faire avant Phase 2.
- Aucune TLS : LAN de dev. Le bearer token entre Carson et mcp-home est défini dans l'environnement de Carson, pas ici.
