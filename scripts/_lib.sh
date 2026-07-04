#!/usr/bin/env bash
# Butlr — helpers communs. Sourcé par tous les autres scripts via `. "$(dirname "$0")/_lib.sh"`.

set -euo pipefail

# -- Couleurs ------------------------------------------------------------------

_cyan='\033[0;36m'
_green='\033[0;32m'
_yellow='\033[1;33m'
_red='\033[0;31m'
_gray='\033[0;90m'
_reset='\033[0m'

log_info() { echo -e "${_cyan}[..] $*${_reset}"; }
log_ok()   { echo -e "${_green}[ok] $*${_reset}"; }
log_warn() { echo -e "${_yellow}[!!] $*${_reset}"; }
log_err()  { echo -e "${_red}[KO] $*${_reset}"; }
log_gray() { echo -e "${_gray}     $*${_reset}"; }

# -- Assertions de prérequis ---------------------------------------------------

assert_cmd() {
    local name="$1"
    local hint="${2:-}"
    if ! command -v "$name" &>/dev/null; then
        log_err "Commande introuvable : $name"
        [ -n "$hint" ] && log_gray "-> $hint"
        exit 1
    fi
    log_ok "$name trouvé ($(command -v "$name"))"
}

ensure_dir() {
    local path="$1"
    if [ ! -d "$path" ]; then
        mkdir -p "$path"
        log_info "Créé : $path"
    fi
}

# -- Chargement de l'environnement ---------------------------------------------

import_btlr_env() {
    local dir
    dir="$(dirname "$(realpath "$0")")"
    local env_file="$dir/env.sh"
    local env_example="$dir/env.example.sh"

    if [ -f "$env_file" ]; then
        # shellcheck source=/dev/null
        . "$env_file"
        log_info "Environnement chargé depuis env.sh"
    elif [ -f "$env_example" ]; then
        # shellcheck source=/dev/null
        . "$env_example"
        log_warn "env.sh absent — utilisation des valeurs par défaut (env.example.sh). Copie env.example.sh -> env.sh pour customiser."
    else
        log_err "Ni env.sh ni env.example.sh trouvés dans $dir"
        exit 1
    fi

    for v in BUTLR_ENV_DIR MODELS_DIR VOICES_DIR; do
        if [ -z "${!v-}" ]; then
            log_err "Variable $v non définie après chargement env."
            exit 1
        fi
    done
}
