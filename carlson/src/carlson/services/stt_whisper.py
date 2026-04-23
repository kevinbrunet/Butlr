"""faster-whisper STT service — slice 2.2.

Frame flow:
    UserStartedSpeakingFrame → clear audio buffer
    InputAudioRawFrame       → append to buffer
    UserStoppedSpeakingFrame → run transcription → emit TranscriptionFrame

Both PTTGate (slice 2.1-2.4) and SileroVADAnalyzer (slice 2.5) produce the
same frame signals, so this processor handles both modes without changes.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Optional

import numpy as np

from pipecat.frames.frames import (
    Frame,
    InputAudioRawFrame,
    StartFrame,
    TranscriptionFrame,
    UserStartedSpeakingFrame,
    UserStoppedSpeakingFrame,
)
from pipecat.processors.frame_processor import FrameDirection, FrameProcessor

from ..config import Config

log = logging.getLogger("carlson.stt")

# Whisper expects 16 kHz mono PCM; must match LocalAudioTransport input rate.
_WHISPER_SAMPLE_RATE = 16_000


class FastWhisperSTTService(FrameProcessor):
    """Transcribes speech segments using faster-whisper (CTranslate2 backend)."""

    def __init__(
        self,
        model_size: str = "large-v3",
        device: str = "cuda",
        compute_type: str = "float16",
    ) -> None:
        super().__init__()
        self._model_size = model_size
        self._device = device
        self._compute_type = compute_type
        self._model = None  # loaded lazily on StartFrame
        self._audio_buf = bytearray()

    # ------------------------------------------------------------------
    # FrameProcessor interface
    # ------------------------------------------------------------------

    async def process_frame(self, frame: Frame, direction: FrameDirection) -> None:
        await super().process_frame(frame, direction)

        if isinstance(frame, StartFrame):
            # Load model in executor so the event loop stays responsive.
            loop = asyncio.get_event_loop()
            await loop.run_in_executor(None, self._load_model)
            await self.push_frame(frame, direction)

        elif isinstance(frame, UserStartedSpeakingFrame):
            self._audio_buf.clear()
            await self.push_frame(frame, direction)

        elif isinstance(frame, InputAudioRawFrame):
            self._audio_buf.extend(frame.audio)
            # Raw audio is not forwarded — STT consumes it entirely.

        elif isinstance(frame, UserStoppedSpeakingFrame):
            # Push the signal first so downstream processors know speech ended,
            # then deliver the transcription if any.
            await self.push_frame(frame, direction)
            if self._audio_buf:
                audio = bytes(self._audio_buf)
                self._audio_buf.clear()
                await self._transcribe_and_push(audio, direction)

        else:
            await self.push_frame(frame, direction)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _load_model(self) -> None:
        from faster_whisper import WhisperModel  # noqa: PLC0415

        log.info(
            "Loading Whisper model '%s' on %s (%s)…",
            self._model_size, self._device, self._compute_type,
        )
        self._model = WhisperModel(
            self._model_size,
            device=self._device,
            compute_type=self._compute_type,
        )
        log.info("Whisper model ready.")

    def _transcribe(self, audio_bytes: bytes) -> tuple[str, str]:
        """Run synchronous transcription; called via run_in_executor."""
        audio_f32 = (
            np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        )
        segments, info = self._model.transcribe(audio_f32, beam_size=5)
        # Consume the lazy generator inside the executor thread.
        text = " ".join(seg.text.strip() for seg in segments).strip()
        return text, info.language

    async def _transcribe_and_push(
        self, audio: bytes, direction: FrameDirection
    ) -> None:
        loop = asyncio.get_event_loop()
        text, lang = await loop.run_in_executor(None, self._transcribe, audio)
        if not text:
            log.debug("[STT] empty transcription — skipping")
            return
        log.info("[STT] %r  (lang=%s)", text, lang)
        # Pass language as a plain string; Pipecat >= 0.0.50 accepts both
        # Language enum and str values in TranscriptionFrame.
        await self.push_frame(
            TranscriptionFrame(text=text, user_id="", timestamp="", language=lang),
            direction,
        )


def build_stt_service(config: Config) -> FastWhisperSTTService:
    return FastWhisperSTTService(model_size=config.stt_model)
