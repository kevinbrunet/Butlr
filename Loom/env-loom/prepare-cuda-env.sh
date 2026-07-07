#!/usr/bin/env bash
set -euo pipefail

# Corrige le décalage ctranslate2/CUDA 13 : ctranslate2 (via faster-whisper) cherche
# libcublas.so.12 en dur, mais CUDA 13 n'installe que libcublas.so.13. Symlink de
# compat + LD_LIBRARY_PATH recréés à chaque appel, sans toucher au shell de l'utilisateur
# (pas de ~/.bashrc) — le symlink ne survit pas à un `rm -rf .venv`.
#
# Usage : ./prepare-cuda-env.sh <commande...>
#   ex.  ./prepare-cuda-env.sh uv run pytest tests/ -q
#        ./prepare-cuda-env.sh uv run python -m loom_orchestrator.bench.harness a --corpus-dir ../corpus

SCRIPT_DIR="$(dirname "$(realpath "$0")")"

if [[ ! -d "$SCRIPT_DIR/.venv" ]]; then
    echo "$SCRIPT_DIR/.venv introuvable — lance 'uv sync --extra dev' d'abord." >&2
    exit 1
fi

CUBLAS_13="$(find "$SCRIPT_DIR/.venv" -name "libcublas.so.13" | head -n1)"

if [[ -z "$CUBLAS_13" ]]; then
    echo "libcublas.so.13 introuvable dans $SCRIPT_DIR/.venv — CUDA 13 est-il bien installé ?" >&2
    exit 1
fi

CUBLAS_DIR="$(dirname "$CUBLAS_13")"
ln -sf libcublas.so.13 "$CUBLAS_DIR/libcublas.so.12"

export LD_LIBRARY_PATH="$CUBLAS_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

exec "$@"
