#!/usr/bin/env bash
# Butlr — télécharge le modèle faster-whisper large-v3 dans butlr-env.
#
# Utilise huggingface_hub (inclus avec faster-whisper) depuis le venv carlson.
# ~ Taille attendue : ~3 GB pour Systran/faster-whisper-large-v3.
# Le modèle est téléchargé une seule fois ; si le dossier cible existe déjà,
# le script sort sans rien faire.

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

dest="${WHISPER_MODEL_DIR:-}"
if [ -z "$dest" ]; then
    log_err "Variable WHISPER_MODEL_DIR non définie. Vérifie env.sh / env.example.sh."
    exit 1
fi

model_bin="$dest/model.bin"
if [ -f "$model_bin" ]; then
    size_mb=$(awk "BEGIN {printf \"%d\", $(stat -c%s "$model_bin") / (1024*1024)}")
    log_ok "Modèle Whisper déjà présent : $dest (model.bin = $size_mb MB)"
    log_gray "Supprime manuellement le dossier pour re-télécharger."
    exit 0
fi

ensure_dir "$dest"

script_dir="$(dirname "$(realpath "$0")")"
venv_python="$script_dir/../carlson/.venv/bin/python"
if [ ! -f "$venv_python" ]; then
    log_err "Venv carlson introuvable : $venv_python"
    log_gray "Lance d'abord depuis le dossier carlson/ :"
    log_gray "  python3 -m venv .venv && . .venv/bin/activate && pip install -e '.[all]'"
    exit 1
fi

log_info "Téléchargement Systran/faster-whisper-large-v3 vers :"
log_info "  $dest"
log_info "(peut prendre plusieurs minutes selon la connexion)"

tmp_script=$(mktemp /tmp/btlr-whisper-XXXXXX.py)
# shellcheck disable=SC2064
trap "rm -f '$tmp_script'" EXIT

cat > "$tmp_script" <<PYEOF
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id="Systran/faster-whisper-large-v3",
    local_dir="${dest}",
)
print("done")
PYEOF

"$venv_python" "$tmp_script"

log_ok "Modèle Whisper disponible : $dest"
