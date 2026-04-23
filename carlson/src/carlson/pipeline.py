"""Pipecat pipeline assembly — slices 2.1 → 2.5.

Frame flow (PTT or VAD mode):

    LocalAudioTransport.input()          → InputAudioRawFrame
    PTTGate  (PTT mode only)             → gates audio; emits User*SpeakingFrame
    FastWhisperSTTService                → TranscriptionFrame
    context_aggregator.user()            → OpenAILLMContextFrame
    OpenAILLMService  (llama.cpp)        → TextFrame (streaming)
    PiperTTSService                      → AudioRawFrame (sentence-level)
    LocalAudioTransport.output()         → speaker
    context_aggregator.assistant()       → updates LLM context

Switching modes:
    PTT (default)  — CARLSON_VAD=0  → PTTGate in pipeline, transport VAD off
    VAD (slice 2.5) — CARLSON_VAD=1 → SileroVADAnalyzer in transport, no PTTGate

See config.py for env-var reference.
"""

from __future__ import annotations

import logging

from pipecat.audio.vad.silero import SileroVADAnalyzer
from pipecat.audio.vad.vad_analyzer import VADParams
from pipecat.pipeline.pipeline import Pipeline
from pipecat.pipeline.task import PipelineParams, PipelineTask
from pipecat.processors.aggregators.openai_llm_context import OpenAILLMContext
from pipecat.transports.local.audio import LocalAudioParams, LocalAudioTransport

from .config import Config
from .mcp_client import McpHomeClient
from .persona import SYSTEM_PROMPT
from .services.llm_local import build_llm_service
from .services.ptt_gate import PTTGate
from .services.stt_whisper import build_stt_service
from .services.tts_piper import build_tts_service

log = logging.getLogger("carlson.pipeline")

# Audio sample rates — must be consistent across transport ↔ services.
_MIC_RATE = 16_000    # Whisper expects 16 kHz
_SPK_RATE = 22_050    # Piper siwis-medium output rate (overridden at voice load)


async def build_pipeline(config: Config, mcp: McpHomeClient) -> PipelineTask:
    """Construct and return a ready-to-run Pipecat PipelineTask."""

    # ------------------------------------------------------------------
    # Audio transport
    # ------------------------------------------------------------------
    if config.use_vad:
        # Slice 2.5 — VAD drives start/stop automatically.
        transport_params = LocalAudioParams(
            audio_in_enabled=True,
            audio_out_enabled=True,
            audio_in_sample_rate=_MIC_RATE,
            audio_out_sample_rate=_SPK_RATE,
            audio_in_channels=1,
            audio_out_channels=1,
            vad_enabled=True,
            vad_analyzer=SileroVADAnalyzer(
                params=VADParams(
                    stop_secs=0.3,          # endpoint ≈ 300 ms after last voice
                )
            ),
            vad_audio_passthrough=True,     # STT still needs the raw frames
        )
    else:
        # Slices 2.1-2.4 — PTTGate handles start/stop via keyboard.
        transport_params = LocalAudioParams(
            audio_in_enabled=True,
            audio_out_enabled=True,
            audio_in_sample_rate=_MIC_RATE,
            audio_out_sample_rate=_SPK_RATE,
            audio_in_channels=1,
            audio_out_channels=1,
            vad_enabled=False,
        )

    transport = LocalAudioTransport(transport_params)

    # ------------------------------------------------------------------
    # Services
    # ------------------------------------------------------------------
    stt = build_stt_service(config)
    llm = build_llm_service(config)
    tts = build_tts_service(config)

    # ------------------------------------------------------------------
    # Conversation context (holds system prompt + history)
    # ------------------------------------------------------------------
    context = OpenAILLMContext(
        messages=[{"role": "system", "content": SYSTEM_PROMPT}]
    )
    context_aggregator = llm.create_context_aggregator(context)

    # ------------------------------------------------------------------
    # Pipeline assembly
    # ------------------------------------------------------------------
    processors: list = [transport.input()]

    if not config.use_vad:
        processors.append(PTTGate())

    processors.extend([
        stt,
        context_aggregator.user(),
        llm,
        tts,
        transport.output(),
        context_aggregator.assistant(),
    ])

    pipeline = Pipeline(processors)

    return PipelineTask(
        pipeline,
        PipelineParams(allow_interruptions=False),
    )
