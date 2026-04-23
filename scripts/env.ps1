# Butlr / Phase 1 — environnement des scripts de setup (PowerShell)
# Copier en env.ps1 puis éditer si besoin. Les scripts dot-sourcent ce fichier.
#
# Utilisation : tous les autres .ps1 font `Import-BtlrEnv` qui dot-source
# env.ps1 s'il existe, sinon env.example.ps1 avec un warning.

# Racine de tout l'outillage ET des modèles. Peut être hors du repo
# (utile si tu veux éviter de trainer des gigas de GGUF dans git).
$env:BUTLR_ENV_DIR    = Join-Path $env:USERPROFILE "butlr-env"

# Sous-dossiers — généralement inutile d'override.
$env:LLAMA_SRC_DIR    = Join-Path $env:BUTLR_ENV_DIR "llama.cpp"
$env:MODELS_DIR       = Join-Path $env:BUTLR_ENV_DIR "models"
$env:VOICES_DIR       = Join-Path $env:BUTLR_ENV_DIR "voices"

# -----------------------------------------------------------------------------
# llama.cpp server — cf. ADR 0006
# -----------------------------------------------------------------------------

# GGUF cible : Qwen 2.5 7B Instruct Q5_K_M par Bartowski (uploader GGUF
# très utilisé, pipeline de quantization reproductible ~).
# Poids attendus : ~5,4 GB. Adapter si tu veux tester Q4_K_M en fallback.
$env:LLAMA_MODEL_URL  = "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q5_K_M.gguf"
$env:LLAMA_MODEL_FILE = Join-Path $env:MODELS_DIR "Qwen2.5-7B-Instruct-Q5_K_M.gguf"

# llama-server runtime
$env:LLAMA_HOST        = "0.0.0.0"
$env:LLAMA_PORT        = "8080"
$env:LLAMA_CTX         = "8192"    # context window (tokens). 16k possible en Q4.
$env:LLAMA_NGL         = "99"      # -ngl : layers offload GPU. 99 = tout.
# ~ --jinja nécessaire pour que le tool calling OpenAI-compat soit bien formatté.
# À confirmer avec la version installée de llama.cpp au moment du build.
$env:LLAMA_EXTRA_FLAGS = "--jinja"

# -----------------------------------------------------------------------------
# Piper TTS — voix
# -----------------------------------------------------------------------------

# Repo officiel des voix Piper (rhasspy) ✓
$env:PIPER_VOICES_BASE_URL = "https://huggingface.co/rhasspy/piper-voices/resolve/main"

# Voix FR + EN retenues (cf. architecture.md §3.1 et carlson/config.py par défaut).
# Chaque voix = 2 fichiers : .onnx (modèle) + .onnx.json (config).
$env:PIPER_VOICE_FR_NAME = "fr_FR-siwis-medium"
$env:PIPER_VOICE_FR_PATH = "fr/fr_FR/siwis/medium"
$env:PIPER_VOICE_EN_NAME = "en_US-amy-medium"
$env:PIPER_VOICE_EN_PATH = "en/en_US/amy/medium"

# -----------------------------------------------------------------------------
# Whisper (faster-whisper)
# -----------------------------------------------------------------------------

$env:WHISPER_MODEL        = "large-v3"      # "large-v3-turbo" ou "medium" comme fallback perf.
$env:WHISPER_DEVICE       = "cuda"
$env:WHISPER_COMPUTE_TYPE = "float16"       # ou "int8_float16" si tension VRAM.
