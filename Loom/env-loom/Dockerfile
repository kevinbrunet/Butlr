# ⚠ Squelette non testé — à valider sur la machine d'exécution (RTX 5090, CUDA) lors de T1.1.
# Image de base CUDA à confirmer selon la version exacte requise par WhisperLiveKit/Sortformer.
# Un seul container : WhisperLiveKit (GPU) et Pocket TTS (CPU) tournent dans le même process
# Python, embarqués en bibliothèque — pas de serveur séparé (cf. ADR-0039).
FROM nvidia/cuda:12.4.1-runtime-ubuntu22.04

RUN apt-get update && apt-get install -y --no-install-recommends \
        python3.12 python3.12-venv curl \
    && rm -rf /var/lib/apt/lists/*

RUN curl -LsSf https://astral.sh/uv/install.sh | sh
ENV PATH="/root/.local/bin:${PATH}"

WORKDIR /app
COPY pyproject.toml .
COPY src/ src/
RUN uv sync

# Registre de voix : embeddings .safetensors montés en volume, jamais construits dans l'image
# (cf. ADR-0036 — pas de traitement wav sur le chemin critique).
VOLUME ["/voices"]

CMD ["uv", "run", "loom-orchestrator"]
