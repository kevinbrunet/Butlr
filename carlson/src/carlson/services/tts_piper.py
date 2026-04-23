"""Piper TTS service — slice 2.4.

Converts LLM text output to speech using the piper-tts Python package.

Streaming strategy: we split the LLM token stream at sentence boundaries
(.!?…) and synthesize each complete sentence immediately. This minimises
TTFT (time-to-first-token perceived by the user) while avoiding choppy
word-level synthesis.

The final sentence fragment (no trailing punctuation) is synthesised when
LLMFullResponseEndFrame arrives, guaranteeing nothing is left unspoken.
"""

from __future__ import annotations

import asyncio
import logging
import re
from pathlib import Path

from pipecat.frames.frames import (
    AudioRawFrame,
    Frame,
    LLMFullResponseEndFrame,
    StartFrame,
    TextFrame,
)
from pipecat.processors.frame_processor import FrameDirection, FrameProcessor

from ..config import Config

log = logging.getLogger("carlson.tts")

# Matches the whitespace that follows a sentence-ending punctuation mark.
# Splitting here preserves the punctuation inside each sentence chunk.
_SENTENCE_SPLIT = re.compile(r"(?<=[.!?…])\s+")


class PiperTTSService(FrameProcessor):
    """Sentence-streaming TTS using a local Piper ONNX voice model."""

    def __init__(self, model_path: str, use_cuda: bool = True) -> None:
        super().__init__()
        self._model_path = model_path
        self._use_cuda = use_cuda
        self._voice = None          # PiperVoice, loaded on StartFrame
        self._sample_rate = 22050   # overwritten once voice is loaded
        self._text_buf = ""         # accumulates LLM tokens between sentences

    # ------------------------------------------------------------------
    # FrameProcessor interface
    # ------------------------------------------------------------------

    async def process_frame(self, frame: Frame, direction: FrameDirection) -> None:
        await super().process_frame(frame, direction)

        if isinstance(frame, StartFrame):
            loop = asyncio.get_event_loop()
            await loop.run_in_executor(None, self._load_voice)
            await self.push_frame(frame, direction)

        elif isinstance(frame, TextFrame):
            self._text_buf += frame.text
            # Flush any complete sentences immediately for low latency.
            parts = _SENTENCE_SPLIT.split(self._text_buf)
            for sentence in parts[:-1]:
                if sentence.strip():
                    await self._speak(sentence.strip(), direction)
            self._text_buf = parts[-1] if parts else ""
            # TextFrame is consumed; do not forward it to the speaker.

        elif isinstance(frame, LLMFullResponseEndFrame):
            # Flush the final sentence fragment (may lack terminal punctuation).
            tail = self._text_buf.strip()
            self._text_buf = ""
            if tail:
                await self._speak(tail, direction)
            await self.push_frame(frame, direction)

        else:
            await self.push_frame(frame, direction)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _load_voice(self) -> None:
        from piper import PiperVoice  # noqa: PLC0415

        log.info("Loading Piper voice from %s…", self._model_path)
        self._voice = PiperVoice.load(self._model_path, use_cuda=self._use_cuda)
        self._sample_rate = self._voice.config.sample_rate
        log.info("Piper voice ready (sample_rate=%d Hz).", self._sample_rate)

    def _synth_raw(self, text: str) -> bytes:
        """Blocking synthesis — run via executor."""
        return b"".join(self._voice.synthesize_stream_raw(text))

    async def _speak(self, text: str, direction: FrameDirection) -> None:
        if not text or self._voice is None:
            return
        log.debug("[TTS] → %r", text)
        loop = asyncio.get_event_loop()
        audio = await loop.run_in_executor(None, self._synth_raw, text)
        if not audio:
            return
        # AudioRawFrame carries raw int16 PCM; num_frames = number of samples.
        await self.push_frame(
            AudioRawFrame(
                audio=audio,
                num_frames=len(audio) // 2,
                num_channels=1,
                sample_rate=self._sample_rate,
            ),
            direction,
        )


# ------------------------------------------------------------------
# Public factory
# ------------------------------------------------------------------

def build_tts_service(config: Config) -> PiperTTSService:
    model_path = _resolve_model(config.tts_model_dir, config.tts_voice_fr)
    return PiperTTSService(model_path=model_path)


def _resolve_model(model_dir: str, voice_name: str) -> str:
    """Return the path to the Piper .onnx file, searching known locations."""
    candidates = [
        Path(model_dir).expanduser() / f"{voice_name}.onnx",
        Path.home() / "butlr-env" / "piper" / "models" / f"{voice_name}.onnx",
        Path.home() / "butlr-env" / "piper" / f"{voice_name}.onnx",
    ]
    for p in candidates:
        if p.exists():
            log.debug("Piper model resolved: %s", p)
            return str(p)
    raise FileNotFoundError(
        f"Piper model '{voice_name}.onnx' not found.\n"
        f"Tried: {[str(p) for p in candidates]}\n"
        f"Set TTS_MODEL_DIR to the directory containing {voice_name}.onnx"
    )
