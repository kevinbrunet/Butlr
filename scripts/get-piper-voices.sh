#!/usr/bin/env bash
# Butlr — download des voix Piper FR + EN.
#
# Chaque voix = 2 fichiers :
#   - <name>.onnx       (modèle)
#   - <name>.onnx.json  (config / speakers / sample rate)
#
# Repo des voix : huggingface.co/rhasspy/piper-voices ✓
# Structure du repo : /<lang_2>/<locale>/<speaker>/<quality>/

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

assert_cmd curl

ensure_dir "$VOICES_DIR"

get_piper_voice() {
    local name="$1"      # ex. fr_FR-gilles-low
    local sub_path="$2"  # ex. fr/fr_FR/gilles/low
    local base="${PIPER_VOICES_BASE_URL}/${sub_path}"
    local dest_dir="$VOICES_DIR/$name"
    ensure_dir "$dest_dir"

    for ext in onnx onnx.json; do
        local file_name="${name}.${ext}"
        local url="${base}/${file_name}"
        local dest="${dest_dir}/${file_name}"

        if [ -f "$dest" ]; then
            log_warn "Déjà présent : $dest"
            continue
        fi

        log_info "Download : $url"
        curl -L --progress-bar --fail -o "$dest" "$url"
        log_ok "OK : $dest"
    done
}

get_piper_voice "fr_FR-mls-medium"        "fr/fr_FR/mls/medium"
get_piper_voice "$PIPER_VOICE_FR_NAME"    "$PIPER_VOICE_FR_PATH"
get_piper_voice "$PIPER_VOICE_EN_NAME"    "$PIPER_VOICE_EN_PATH"

log_ok "Voix Piper téléchargées dans : $VOICES_DIR"
