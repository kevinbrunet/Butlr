"""Pipecat pipeline assembly.

Frame flow (see docs/architecture.md §4):

    mic → wake_word → vad → stt → llm → tts → speaker
                                    ↑↓
                               mcp_bridge
                                    ↕
                           filler_sidecar (observes tool calls)

This file wires the components; the per-component logic lives in services/.
"""

from __future__ import annotations

from .config import Config
from .filler import FillerSidecar
from .mcp_client import McpHomeClient
from .persona import SYSTEM_PROMPT


async def build_pipeline(config: Config, mcp: McpHomeClient):
    """Construct and return a configured Pipecat pipeline.

    This is a skeleton. Concrete wiring depends on the installed Pipecat
    version — fill in once deps are pinned.
    """
    # from pipecat.pipeline.pipeline import Pipeline
    # from .services.stt_whisper import build_stt_service
    # from .services.tts_piper import build_tts_service
    # from .services.llm_local import build_llm_service
    # from .services.wake_word import build_wake_word_service

    _ = SYSTEM_PROMPT
    _ = FillerSidecar(delay_ms=config.filler_delay_ms, language=config.language_default)
    _ = mcp  # will be attached to the LLM service as tool provider

    # Expected assembly once SDK is pinned:
    #   pipeline = Pipeline([
    #       mic_input,
    #       wake_word,
    #       vad,
    #       stt,
    #       context_manager(system_prompt=SYSTEM_PROMPT),
    #       llm.with_tools(mcp.tools_as_openai()),
    #       filler_sidecar,
    #       tts,
    #       speaker_output,
    #   ])
    #   return pipeline
    raise NotImplementedError("Pipeline wiring — see TODO markers.")
