"""LLM service — OpenAI-compatible client pointing at a local llama.cpp server.

llama.cpp server (`llama-server`) exposes /v1/chat/completions in OpenAI
format (cf. ADR 0006). Pipecat's OpenAILLMService accepts any base_url,
so no custom wrapper is needed.

Tip: start llama-server with --jinja to get correct tool-call formatting
for Qwen 2.5 Instruct's chat template (cf. ADR 0006).
"""

from __future__ import annotations

from pipecat.services.openai import OpenAILLMService

from ..config import Config


def build_llm_service(config: Config) -> OpenAILLMService:
    """Return a Pipecat LLM service targeting the local llama.cpp server."""
    return OpenAILLMService(
        api_key=config.llm_api_key,
        model=config.llm_model,
        base_url=config.llm_base_url,
    )
