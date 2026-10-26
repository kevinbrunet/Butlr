#!/usr/bin/env bash
set -euo pipefail

# Sert Voxtral Realtime pour les variantes `--stt voxtral` de Loom (ADR-0049).
# Usage : ./serve.sh            (premier lancement : télécharge ~9 Go de poids)
#
# Réglages mémoire : vLLM réserve par défaut 90 % de la VRAM, ce qui affamerait le reste du
# pipeline Loom (traduction llama.cpp, Pocket TTS, et en multi la séparation/l'embedding) sur
# la même carte. On le borne :
#   --gpu-memory-utilization : part de la VRAM pour vLLM (poids BF16 ~9 Go + cache KV).
#   --max-model-len          : longueur de session ; ✓ la fiche indique >= 45 000 pour 1 h
#                              (~12,5 jetons/s), 8 192 couvre donc ~11 min par session.
# ✓ Constaté le 2026-10-26 (vLLM 0.32.0, RTX 5090) : 16 384 jetons demandent 5,72 Gio de cache
#   KV, et 0,40 de la VRAM n'en laisse que 2,18 Gio après les poids — démarrage refusé.
#   0,50 / 8 192 : ~2,9 Gio de cache nécessaires. ⚠ Laisse ~16 Go au reste de Loom : assez
#   pour mono, juste pour multi (13 à 27 Go mesurés selon l'étage).
SCRIPT_DIR="$(dirname "$(realpath "$0")")"
cd "$SCRIPT_DIR"

export VLLM_DISABLE_COMPILE_CACHE=1
# ✓ Constaté le 2026-10-26 (vLLM 0.32.0, fedora3) : l'échantillonneur FlashInfer est compilé à
#   la volée au premier appel et échoue faute d'en-têtes CUDA (`curand.h: No such file or
#   directory`, pas de toolkit CUDA système). Repli sur l'échantillonneur PyTorch — sans enjeu
#   ici, la transcription se fait à température 0.
export VLLM_USE_FLASHINFER_SAMPLER=0
# `--tokenizer-mode mistral` : exigé par la recette vLLM du modèle (lue le 2026-10-26) ; vLLM
# 0.32.0 démarrait sans, et la sortie mesurée est identique avec ou sans (sonde ponctuation).
# VOXTRAL_MODEL : identifiant HF ou dossier local (ex. une copie avec un autre
# `transcription_delay_ms` dans tekken.json) ; arguments supplémentaires passés tels quels.
exec uv run vllm serve "${VOXTRAL_MODEL:-mistralai/Voxtral-Mini-4B-Realtime-2602}" \
    --host 127.0.0.1 \
    --port "${VOXTRAL_PORT:-8100}" \
    --gpu-memory-utilization "${VOXTRAL_GPU_MEM:-0.50}" \
    --max-model-len "${VOXTRAL_MAX_LEN:-8192}" \
    --tokenizer-mode mistral \
    --compilation_config '{"cudagraph_mode": "PIECEWISE"}' \
    "$@"
