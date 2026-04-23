"""LLM service — OpenAI-compatible client pointant sur le llama.cpp server local.

llama-server expose /v1/chat/completions au format OpenAI (cf. ADR 0006).
Pipecat's OpenAILLMService accepte n'importe quelle base_url — aucune
adaptation nécessaire côté Carlson.

~ Flag --jinja OBLIGATOIRE au démarrage de llama-server pour que le
  tool calling soit correctement formaté dans les réponses (Qwen 2.5 template).
  Sans ce flag, les appels d'outils sont ignorés silencieusement ⚠.

Streaming activé par défaut dans OpenAILLMService — important pour le TTFT
perçu (le TTS peut démarrer dès les premiers tokens).
"""

from __future__ import annotations

import logging

from ..config import Config

log = logging.getLogger("carlson.llm")


def build_llm_service(config: Config):
    """Return a Pipecat LLM service targeting the local llama.cpp server.

    ~ OpenAILLMService import path stable depuis pipecat 0.0.40.
    """
    from pipecat.services.openai import OpenAILLMService

    log.info("LLM service → %s  model=%s", config.llm_base_url, config.llm_model)
    return OpenAILLMService(
        base_url=config.llm_base_url,
        api_key=config.llm_api_key,  # llama.cpp l'ignore, OpenAI SDK l'exige
        model=config.llm_model,
    )
