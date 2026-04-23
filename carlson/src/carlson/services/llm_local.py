"""LLM service — OpenAI-compatible client pointing at a local llama.cpp server.

llama.cpp server (`llama-server`) expose `/v1/chat/completions` et
`/v1/completions` au format OpenAI (cf. ADR 0006). Pipecat `OpenAILLMService`
accepte n'importe quelle `base_url`, on réutilise tel quel.

~ flag `--jinja` à passer au démarrage de llama-server pour que le tool calling
soit correctement formaté dans les réponses — à confirmer avec la version
installée.
"""

from __future__ import annotations

from ..config import Config


def build_llm_service(config: Config):
    """Return a Pipecat LLM service targeting the local llama.cpp server endpoint."""
    # from pipecat.services.openai import OpenAILLMService
    # return OpenAILLMService(
    #     base_url=config.llm_base_url,
    #     api_key=config.llm_api_key,
    #     model=config.llm_model,
    # )
    raise NotImplementedError("Pipecat LLM wiring — pin the SDK version first.")
