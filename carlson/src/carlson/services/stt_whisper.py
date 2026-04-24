from __future__ import annotations

import logging
from collections.abc import AsyncGenerator
from typing import Any

from ..config import Config

log = logging.getLogger("carlson.stt")


def build_stt_service(config: Config):
    import numpy as np
    from faster_whisper import WhisperModel
    from pipecat.frames.frames import Frame, TranscriptionFrame
    from pipecat.services.settings import STTSettings
    from pipecat.services.stt_service import SegmentedSTTService

    class FasterWhisperSTTService(SegmentedSTTService):
        def __init__(self, model_size: str, device: str, compute_type: str) -> None:
            # model et language doivent être initialisés (pas NOT_GIVEN) — Pipecat 1.0 validate_complete
            super().__init__(settings=STTSettings(model=model_size, language=None))
            self._model = WhisperModel(model_size, device=device, compute_type=compute_type, local_files_only=True)

        async def run_stt(self, audio: bytes) -> AsyncGenerator[Frame, None]:
            # Pipecat 1.0 : run_stt est un async generator qui yield des frames,
            # pas une méthode qui retourne une str.
            audio_np = np.frombuffer(audio, dtype=np.int16).astype(np.float32) / 32768.0
            segments, _ = self._model.transcribe(audio_np, language=None)
            text = "".join(s.text for s in segments).strip()
            if text:
                yield TranscriptionFrame(text=text, user_id="", timestamp="")

    return FasterWhisperSTTService(
        model_size=config.stt_model,
        device="cuda",
        compute_type="float16",
    )