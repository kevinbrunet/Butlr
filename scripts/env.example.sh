#!/usr/bin/env bash
# Butlr — environnement des scripts de setup (Bash / Linux).
# Copier en env.sh puis éditer si besoin. Les scripts sourcent ce fichier.

# Racine de tout l'outillage ET des modèles. Peut être hors du repo.
export BUTLR_ENV_DIR="$HOME/butlr-env"

# Sous-dossiers — généralement inutile d'override.
export MODELS_DIR="$BUTLR_ENV_DIR/models"
export VOICES_DIR="$BUTLR_ENV_DIR/voices"

# -----------------------------------------------------------------------------
# LLM distant — cf. ADR 0006
# -----------------------------------------------------------------------------

# Serveur llama.cpp (ou compatible OpenAI) qui tourne sur le LAN.
export LLM_BASE_URL="http://192.168.1.85:8083/v1"

# -----------------------------------------------------------------------------
# Piper TTS — voix
# -----------------------------------------------------------------------------

# Repo officiel des voix Piper (rhasspy) ✓
export PIPER_VOICES_BASE_URL="https://huggingface.co/rhasspy/piper-voices/resolve/main"

export PIPER_VOICE_FR_NAME="fr_FR-gilles-low"
export PIPER_VOICE_FR_PATH="fr/fr_FR/gilles/low"
export PIPER_VOICE_EN_NAME="en_GB-alan-medium"
export PIPER_VOICE_EN_PATH="en/en_GB/alan/medium"

# -----------------------------------------------------------------------------
# Whisper (faster-whisper)
# -----------------------------------------------------------------------------

export WHISPER_MODEL_DIR="$MODELS_DIR/whisper/faster-whisper-large-v3"
export STT_MODEL="$WHISPER_MODEL_DIR"
export WHISPER_DEVICE="cuda"
export WHISPER_COMPUTE_TYPE="float16"    # ou "int8_float16" si tension VRAM.
