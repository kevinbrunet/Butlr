"""Pipecat pipeline assembly.

Frame flow (Phase 2, Slice 1 — STT → LLM → TTS, pas de tool) :

    mic → [PTT gate | VAD] → STT → context_agg.user → LLM → TTS → speaker
                                                               ↕
                                                      context_agg.assistant

Wake word et MCP tools câblés en phases ultérieures.

~ Les chemins d'import Pipecat dépendent de la version pinnée. Si une import
  rate, faire `python -c "import pipecat; print(pipecat.__version__)"` et
  ajuster en conséquence.
"""

from __future__ import annotations

import logging
from typing import Any

from .config import Config
from .mcp_client import McpHomeClient
from .persona import SYSTEM_PROMPT
from .services.llm_local import build_llm_service
from .services.stt_whisper import build_stt_service
from .services.tts_piper import build_tts_service

log = logging.getLogger("carlson.pipeline")


def _make_ptt_gate() -> Any:
    """Build a PushToTalkGate as a proper FrameProcessor subclass.

    Returned gate exposes open() and close() coroutines callable from any
    thread via asyncio.run_coroutine_threadsafe.
    """
    # ~ FrameProcessor base class — import path stable since pipecat 0.0.40
    from pipecat.frames.frames import AudioRawFrame, UserStartedSpeakingFrame, UserStoppedSpeakingFrame
    from pipecat.processors.frame_processor import FrameProcessor

    class PushToTalkGate(FrameProcessor):
        def __init__(self) -> None:
            super().__init__()
            self._open = False

        async def open(self) -> None:
            if not self._open:
                self._open = True
                await self.push_frame(UserStartedSpeakingFrame())
                log.debug("PTT gate ouverte")

        async def close(self) -> None:
            if self._open:
                self._open = False
                await self.push_frame(UserStoppedSpeakingFrame())
                log.debug("PTT gate fermée → transcription déclenchée")

        async def process_frame(self, frame, direction):
            if isinstance(frame, AudioRawFrame) and not self._open:
                return  # discard mic audio while gate is closed
            await self.push_frame(frame, direction)

    return PushToTalkGate()


def _make_transcription_logger() -> Any:
    """Inline FrameProcessor that logs TranscriptionFrame — satisfies étape 2.2."""
    from pipecat.frames.frames import TranscriptionFrame
    from pipecat.processors.frame_processor import FrameProcessor

    class TranscriptionLogger(FrameProcessor):
        async def process_frame(self, frame, direction):
            if isinstance(frame, TranscriptionFrame):
                log.info("STT › %s", frame.text)
            await self.push_frame(frame, direction)

    return TranscriptionLogger()


def _make_llm_response_logger() -> Any:
    """Inline FrameProcessor that logs LLM text frames — satisfies étape 2.3."""
    from pipecat.frames.frames import TextFrame
    from pipecat.processors.frame_processor import FrameProcessor

    class LLMResponseLogger(FrameProcessor):
        async def process_frame(self, frame, direction):
            if isinstance(frame, TextFrame) and frame.text:
                log.info("LLM › %s", frame.text.rstrip())
            await self.push_frame(frame, direction)

    return LLMResponseLogger()


async def build_pipeline(config: Config, mcp: McpHomeClient) -> tuple[Any, Any]:
    """Construct and return a (Pipeline, gate) pair.

    gate is None when use_vad=True ; it's a PushToTalkGate when use_vad=False.

    ~ Import paths validated against pipecat-ai 0.0.50+. If imports fail after
      a version bump, check `pipecat.transports`, `pipecat.audio`, and
      `pipecat.processors` for renames.
    """
    # ~ pipecat core imports
    from pipecat.pipeline.pipeline import Pipeline
    from pipecat.processors.aggregators.llm_context import LLMContext, LLMContextAggregatorPair
    from pipecat.transports.local.audio import LocalAudioTransport, LocalAudioTransportParams

    stt = build_stt_service(config)
    llm = build_llm_service(config)
    tts = build_tts_service(config)

    # MCP tools — pas câblés en Phase 2 (pas de tool calling). Sera passé au LLM
    # service en Phase 3 via llm.set_tools(mcp.tools_as_openai()).
    _ = mcp

    # ~ create_context_aggregator pattern — stable depuis pipecat 1.0.0
    context = LLMContext(messages=[{"role": "system", "content": SYSTEM_PROMPT}])
    context_aggregator: LLMContextAggregatorPair = llm.create_context_aggregator(context)

    transcription_logger = _make_transcription_logger()
    llm_response_logger = _make_llm_response_logger()

    gate: Any = None

    if config.use_vad:
        # Étape 2.5 — Silero VAD remplace le push-to-talk
        # ~ SileroVADAnalyzer : nécessite silero-vad>=5.0 et torch
        from pipecat.audio.vad.silero import SileroVADAnalyzer

        transport = LocalAudioTransport(
            LocalAudioTransportParams(
                audio_in_enabled=True,
                audio_out_enabled=True,
                vad_enabled=True,
                vad_analyzer=SileroVADAnalyzer(),
                # ~ vad_audio_passthrough : le audio brut est quand même propagé
                # en aval pour que le STT l'accumule.
                vad_audio_passthrough=True,
            )
        )
        pipeline = Pipeline([
            transport.input(),
            transcription_logger,
            stt,
            context_aggregator.user(),
            llm,
            llm_response_logger,
            tts,
            transport.output(),
            context_aggregator.assistant(),
        ])
    else:
        # Étapes 2.1–2.4 — push-to-talk clavier
        transport = LocalAudioTransport(
            LocalAudioTransportParams(
                audio_in_enabled=True,
                audio_out_enabled=True,
                vad_enabled=False,
            )
        )
        gate = _make_ptt_gate()
        pipeline = Pipeline([
            transport.input(),
            gate,
            stt,
            transcription_logger,
            context_aggregator.user(),
            llm,
            llm_response_logger,
            tts,
            transport.output(),
            context_aggregator.assistant(),
        ])

    log.info(
        "Pipeline construit (mode=%s, stt=%s, llm=%s@%s, tts=%s)",
        "vad" if config.use_vad else "ptt",
        config.stt_model,
        config.llm_model,
        config.llm_base_url,
        config.tts_engine,
    )
    return pipeline, gate
