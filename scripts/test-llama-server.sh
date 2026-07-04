#!/usr/bin/env bash
# Butlr — smoke test HTTP du serveur LLM distant.
#
# Vérifie :
#   1. /v1/models répond 200 avec au moins un modèle.
#   2. /v1/chat/completions renvoie une complétion non vide sur un prompt court.

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

assert_cmd curl
assert_cmd jq

base="${LLM_BASE_URL%/}"   # retire le slash final si présent

# -- /v1/models ---------------------------------------------------------------
log_info "GET $base/models"
if ! models_raw=$(curl -s --fail "$base/models" 2>&1); then
    log_err "Serveur injoignable sur $base. Il est accessible depuis cette machine ?"
    exit 1
fi

model_id=$(echo "$models_raw" | jq -r '.data[0].id // empty')
if [ -z "$model_id" ]; then
    log_err "Aucun modèle listé par le serveur. Réponse : $models_raw"
    exit 1
fi
log_ok "Modèle servi : $model_id"

# -- /v1/chat/completions -----------------------------------------------------
log_info "POST $base/chat/completions (prompt court)"

payload=$(jq -n \
    --arg model "$model_id" \
    --arg content "Réponds juste 'pong'." \
    '{model: $model, messages: [{role: "user", content: $content}], max_tokens: 32, temperature: 0}')

if ! resp=$(echo "$payload" | curl -s --fail -X POST "$base/chat/completions" \
        -H "Content-Type: application/json" \
        --data-binary "@-" 2>&1); then
    log_err "chat/completions échoué."
    exit 1
fi

content=$(echo "$resp" | jq -r '.choices[0].message.content // empty')
if [ -z "$content" ]; then
    log_err "Réponse vide ou structure inattendue. Raw : $resp"
    exit 1
fi

log_ok "Réponse : $content"
log_ok "Serveur LLM opérationnel ✓"
