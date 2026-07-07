#!/usr/bin/env bash
# Butlr — smoke test Piper TTS sur FR + EN.
#
# Suppose que `piper` est sur le PATH (venv activé ou installé globalement).
#
# Usage :
#   . ../carson/.venv/bin/activate
#   ./get-piper-voices.sh
#   ./test-piper.sh
#
# Référence CLI Piper : https://github.com/rhasspy/piper ~

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

assert_cmd piper "Dans un venv activé : pip install piper-tts"

invoke_piper_say() {
    local voice_name="$1"
    local text="$2"
    local out_wav="$3"
    local model_path="$VOICES_DIR/$voice_name/$voice_name.onnx"

    if [ ! -f "$model_path" ]; then
        log_err "Modèle voix introuvable : $model_path (lance ./get-piper-voices.sh)"
        exit 1
    fi

    log_info "Synthèse '$voice_name' -> $out_wav"
    # Piper lit le texte sur stdin, écrit le WAV sur --output_file.
    echo "$text" | piper --model "$model_path" --output_file "$out_wav"

    if [ ! -f "$out_wav" ]; then
        log_err "WAV non généré : $out_wav"
        exit 1
    fi

    size_kb=$(awk "BEGIN {printf \"%.1f\", $(stat -c%s "$out_wav") / 1024}")
    log_ok "OK : $out_wav ($size_kb KB)"
}

out_dir="$BUTLR_ENV_DIR/piper-samples"
ensure_dir "$out_dir"

invoke_piper_say \
    "$PIPER_VOICE_FR_NAME" \
    "Bonjour, je suis Carson. Comment puis-je vous servir ?" \
    "$out_dir/test-fr.wav"

invoke_piper_say \
    "$PIPER_VOICE_EN_NAME" \
    "Good evening, I am Carson. How may I help you today?" \
    "$out_dir/test-en.wav"

log_ok "Piper opérationnel. Échantillons : $out_dir"
log_gray "Lis-les avec : xdg-open '$out_dir'"
