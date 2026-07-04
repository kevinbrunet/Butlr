#!/usr/bin/env bash
# Butlr / Phase 4 — Entraîne le wake word "Hey Carlson" via Docker.
#
# Lance un container Linux (openWakeWord + Piper TTS) qui :
#   1. Génère ~5 000 clips audio "Hey Carlson" via Piper TTS
#   2. Télécharge les données négatives depuis Hugging Face
#   3. Entraîne le modèle et produit hey_carlson.tflite
#
# Prérequis : Docker installé et en cours d'exécution.
# GPU       : si NVIDIA Container Toolkit configuré,
#             passer --gpu pour accélérer l'entraînement (~45 min vs ~4 h CPU).
#
# Usage :
#   ./train-wakeword.sh                    # entraînement CPU
#   ./train-wakeword.sh --gpu              # entraînement GPU (NVIDIA requis)
#   ./train-wakeword.sh --generate-config  # génère le YAML de config seulement
#   ./train-wakeword.sh --rebuild-image    # force le rebuild de l'image Docker

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

gpu=false
generate_config=false
rebuild_image=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --gpu)             gpu=true;             shift ;;
        --generate-config) generate_config=true; shift ;;
        --rebuild-image)   rebuild_image=true;   shift ;;
        *) log_err "Argument inconnu : $1"; exit 1 ;;
    esac
done

repo_root="$(realpath "$(dirname "$(realpath "$0")")/..")"
carlson_dir="$repo_root/carlson"
assets_dir="$carlson_dir/assets/wakeword"
config_path="$assets_dir/training_config.yaml"
# Cache des features HuggingFace (~4-6 GB) — persistant entre les runs.
# Stocké hors du repo (trop volumineux pour git).
features_dir="${XDG_DATA_HOME:-$HOME/.local/share}/butlr/wakeword-features"
image_name="butlr-wakeword-train"
dockerfile="carlson/docker/Dockerfile.wakeword-train"

# -- Vérifie Docker -----------------------------------------------------------
assert_cmd docker "https://docs.docker.com/engine/install/fedora/"

if ! docker info &>/dev/null; then
    log_err "Docker daemon ne répond pas. Lance Docker et réessaie."
    exit 1
fi

# -- Mode : génération de config seule ----------------------------------------
if $generate_config; then
    log_info "Génération du fichier de config YAML..."

    venv_python="$carlson_dir/.venv/bin/python"
    train_script="$carlson_dir/scripts/train_wakeword.py"

    if [ ! -f "$venv_python" ]; then
        log_err "venv carlson introuvable : $venv_python"
        log_gray "Crée-le d'abord avec pip install -e '.[all,dev]'"
        exit 1
    fi

    "$venv_python" "$train_script" --generate-config

    echo ""
    log_ok "Config prête : $config_path"
    echo "Lance l'entraînement : ./train-wakeword.sh"
    exit 0
fi

# -- Génère la config si absente -----------------------------------------------
if [ ! -f "$config_path" ]; then
    log_warn "training_config.yaml absent — génération automatique..."

    venv_python="$carlson_dir/.venv/bin/python"
    train_script="$carlson_dir/scripts/train_wakeword.py"

    if [ -f "$venv_python" ]; then
        "$venv_python" "$train_script" --generate-config
    else
        mkdir -p "$assets_dir"
        cat > "$config_path" <<'YAML'
model_name: hey_carlson
target_phrase: "Hey Carlson"
n_positive_samples: 5000
n_epochs: 100
detection_threshold: 0.5
use_precomputed_features: true
YAML
        log_warn "Config minimale écrite dans $config_path"
    fi
fi

# -- Build de l'image Docker --------------------------------------------------
if ! docker image inspect "$image_name" &>/dev/null || $rebuild_image; then
    log_info "Build de l'image Docker $image_name..."
    log_gray "(~5-10 min, ~4-6 GB téléchargés — uniquement au premier build)"

    docker build -f "$dockerfile" -t "$image_name" "$repo_root"
    log_ok "Image construite : $image_name"
else
    log_gray "Image $image_name déjà présente (utilise --rebuild-image pour forcer)."
fi

# -- Lancement de l'entraînement -----------------------------------------------
assets_dir_abs="$(realpath "$assets_dir")"
mkdir -p "$features_dir"

docker_args=(
    run --rm
    --name butlr-wakeword-train
    --shm-size=4g
    -v "${assets_dir_abs}:/data"
    -v "${features_dir}:/work/features"
)

if $gpu; then
    log_info "Mode GPU activé (--gpus all)."
    docker_args+=(--gpus all)
fi

docker_args+=("$image_name")

echo ""
log_info "Lancement de l'entraînement..."
log_gray "Config    : $config_path"
log_gray "Features  : $features_dir (cache persistant, ~4-6 GB au 1er run)"
log_gray "Sortie    : $assets_dir/hey_carlson.tflite"
if $gpu; then
    log_gray "Durée     : ~45 min (GPU)"
else
    log_gray "Durée     : ~2-4 h (CPU) — ajoute --gpu si tu as NVIDIA Container Toolkit"
fi
echo ""

docker "${docker_args[@]}"

# -- Résultat ------------------------------------------------------------------
tflite="$assets_dir/hey_carlson.tflite"
if [ -f "$tflite" ]; then
    size_kb=$(( $(stat -c%s "$tflite") / 1024 ))
    echo ""
    log_ok "Modèle prêt : $tflite ($size_kb KB)"
    echo ""
    echo "Active le wake word :"
    echo "  export USE_WAKEWORD=1"
    echo "  carlson"
else
    log_warn "hey_carlson.tflite introuvable après l'entraînement. Vérifie les logs."
fi
