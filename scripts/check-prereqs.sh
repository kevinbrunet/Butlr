#!/usr/bin/env bash
# Butlr — vérifie les prérequis système.
#
# Prérequis attendus (Fedora Workstation) :
#   - GPU NVIDIA avec drivers récents ✓ (pour faster-whisper CUDA)
#   - git, python3, curl, jq ✓
#
# Usage :
#   cd scripts && ./check-prereqs.sh

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

log_info "Vérification des prérequis..."

# -- GPU / drivers (faster-whisper tourne en CUDA) ----------------------------
assert_cmd nvidia-smi "Installer les drivers NVIDIA : sudo dnf install akmod-nvidia (rpmfusion-nonfree)"

log_info "GPU / drivers :"
nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader | while IFS= read -r line; do
    log_gray "$line"
done

# -- Outillage de base ---------------------------------------------------------
assert_cmd git     "sudo dnf install git"
assert_cmd python3 "sudo dnf install python3"
assert_cmd curl    "sudo dnf install curl"
assert_cmd jq      "sudo dnf install jq"

py_version=$(python3 --version 2>&1)
log_info "Python : $py_version"

# -- Serveur LLM distant -------------------------------------------------------
log_info "Test de joignabilité du serveur LLM : $LLM_BASE_URL"
if curl -s --fail --max-time 3 "${LLM_BASE_URL%/}/models" &>/dev/null; then
    log_ok "Serveur LLM joignable."
else
    log_warn "Serveur LLM injoignable sur $LLM_BASE_URL — vérifie le réseau ou lance ./test-llama-server.sh pour le détail."
fi

# -- Dossiers d'environnement -------------------------------------------------
log_info "Dossiers Butlr :"
for d in "$BUTLR_ENV_DIR" "$MODELS_DIR" "$VOICES_DIR"; do
    ensure_dir "$d"
    log_gray "$d"
done

log_ok "Tous les prérequis sont OK."
