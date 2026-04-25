"""Pipecat pipeline assembly.

Frame flow (Phase 4, Slice 3 — wake word + STT → LLM (+tools MCP) → TTS) :

    mic → [WakeWord | PTT gate | VAD] → STT → context_agg.user → LLM → TTS → speaker
                                                                    ↕         ↕
                                                              tool_call   context_agg.assistant
                                                                 ↓
                                                            McpHomeClient.call()

Modes :
    use_wakeword=True  → WakeWord (+ Silero VAD pour fin de tour)
    use_vad=True       → Silero VAD seul (sans wake word)
    use_vad=False      → push-to-talk clavier

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
from .services.wake_word import build_wake_word_service

log = logging.getLogger("carlson.pipeline")


def _make_ptt_gate() -> Any:
    """Build a PushToTalkGate as a proper FrameProcessor subclass.

    Returned gate exposes open() and close() coroutines callable from any
    thread via asyncio.run_coroutine_threadsafe.
    """
    # ~ FrameProcessor base class — import path stable since pipecat 0.0.40
    # SegmentedSTTService écoute VADUser*SpeakingFrame, pas UserStartedSpeakingFrame
    # (classes distinctes sans héritage commun — Pipecat 1.0).
    from pipecat.frames.frames import AudioRawFrame, VADUserStartedSpeakingFrame, VADUserStoppedSpeakingFrame
    from pipecat.processors.frame_processor import FrameProcessor

    class PushToTalkGate(FrameProcessor):
        def __init__(self) -> None:
            super().__init__()
            self._open = False
        async def _start(self, frame, direction):
            await super()._start(frame, direction)

        async def open(self) -> None:
            if not self._open:
                self._open = True
                await self.push_frame(VADUserStartedSpeakingFrame())
                log.debug("PTT gate ouverte")

        async def close(self) -> None:
            if self._open:
                self._open = False
                await self.push_frame(VADUserStoppedSpeakingFrame())
                log.debug("PTT gate fermée → transcription déclenchée")

        async def process_frame(self, frame, direction):
            await super().process_frame(frame, direction)

            # Bloquer uniquement l'audio quand la gate est fermée.
            # StartFrame/EndFrame/CancelFrame doivent toujours se propager en aval
            # sinon les services downstream (STT…) ne reçoivent jamais StartFrame
            # et rejettent toutes les frames suivantes (Pipecat 1.0 _check_started).
            if isinstance(frame, AudioRawFrame) and not self._open:
                return

            await self.push_frame(frame, direction)

    return PushToTalkGate()


def _make_transcription_logger() -> Any:
    """Inline FrameProcessor that logs TranscriptionFrame — satisfies étape 2.2."""
    from pipecat.frames.frames import TranscriptionFrame
    from pipecat.processors.frame_processor import FrameProcessor

    class TranscriptionLogger(FrameProcessor):
        async def process_frame(self, frame, direction):
            await super().process_frame(frame, direction)
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
            await super().process_frame(frame, direction)
            if isinstance(frame, TextFrame) and frame.text:
                log.info("LLM › %s", frame.text.rstrip())
            await self.push_frame(frame, direction)

    return LLMResponseLogger()


async def build_pipeline(config: Config, mcp: McpHomeClient) -> tuple[Any, Any]:
    """Construct and return a (Pipeline, gate) pair.

    gate is a PushToTalkGate when use_vad=False and use_wakeword=False ; None sinon.

    ~ Import paths validated against pipecat-ai 0.0.50+. If imports fail after
      a version bump, check `pipecat.transports`, `pipecat.audio`, and
      `pipecat.processors` for renames.
    """
    # ~ pipecat core imports
    from pipecat.pipeline.pipeline import Pipeline
    from pipecat.processors.aggregators.llm_response_universal import LLMContext, LLMContextAggregatorPair
    from pipecat.transports.local.audio import LocalAudioTransport, LocalAudioTransportParams

    from pipecat.services.llm_service import FunctionCallParams

    stt = build_stt_service(config)
    llm = build_llm_service(config)
    tts = build_tts_service(config)

    # MCP tools — Phase 3 : passer les schemas au LLM et enregistrer le handler.
    tools_schema = mcp.tools_as_pipecat()

    if tools_schema is not None:
        async def _handle_tool_call(params: FunctionCallParams) -> None:
            result = await mcp.call(params.function_name, dict(params.arguments))
            await params.result_callback(result)

        llm.register_function(None, _handle_tool_call)
        log.info("Tool calling activé (%d tools)", len(mcp.tools_as_openai()))
    else:
        log.info("Aucun tool MCP disponible — mode texte seul.")

    # ~ create_context_aggregator pattern — stable depuis pipecat 1.0.0
    # tools omis si None (LLMContext attend NOT_GIVEN, pas None).
    context = LLMContext(
        messages=[{"role": "system", "content": SYSTEM_PROMPT}],
        **({"tools": tools_schema} if tools_schema is not None else {}),
    )
    context_aggregator = LLMContextAggregatorPair(context)

    transcription_logger = _make_transcription_logger()
    llm_response_logger = _make_llm_response_logger()

    gate: Any = None

    # ~ SileroVADAnalyzer : nécessite silero-vad>=5.0 et torch.
    # Réutilisé en mode wake word (Silero gère la fin de tour) et en mode VAD pur.
    def _make_vad_transport() -> Any:
        from pipecat.audio.vad.silero import SileroVADAnalyzer

        return LocalAudioTransport(
            LocalAudioTransportParams(
                audio_in_enabled=True,
                audio_out_enabled=True,
                vad_enabled=True,
                vad_analyzer=SileroVADAnalyzer(),
                # ~ vad_audio_passthrough : l'audio brut se propage en aval pour
                # que le STT accumule les échantillons entre les events VAD.
                vad_audio_passthrough=True,
            )
        )

    if config.use_wakeword:
        # Phase 4 — Slice 3 : wake word "Hey Carlson" en amont du STT.
        # Silero reste actif pour détecter la fin du tour (VADUserStoppedSpeakingFrame).
        transport = _make_vad_transport()
        wake_word = build_wake_word_service(config)
        pipeline = Pipeline([
            transport.input(),
            wake_word,
            stt,
            transcription_logger,
            context_aggregator.user(),
            llm,
            llm_response_logger,
            tts,
            transport.output(),
            context_aggregator.assistant(),
        ])
        mode = "wakeword"
    elif config.use_vad:
        # Étape 2.5 — Silero VAD remplace le push-to-talk
        transport = _make_vad_transport()
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
        mode = "vad"
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
        mode = "ptt"

    log.info(
        "Pipeline construit (mode=%s, stt=%s, llm=%s@%s, tts=%s)",
        mode,
        config.stt_model,
        config.llm_model,
        config.llm_base_url,
        config.tts_engine,
    )
    return pipeline, gate
