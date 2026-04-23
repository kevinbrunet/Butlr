from __future__ import annotations
import logging
from ..config import Config
log = logging.getLogger("carlson.stt")

def build_stt_service(config: Config):
    from faster_whisper import WhisperModel
    from pipecat.services.stt_service import SegmentedSTTService
    from pipecat.frames.frames import TranscriptionFrame, AudioRawFrame
    import numpy as np

    class FasterWhisperSTTService(SegmentedSTTService):
        def __init__(self, model_size, device, compute_type):
            from pipecat.services.settings import STTSettings
            # model et language doivent être initialisés (pas NOT_GIVEN) — Pipecat 1.0 validate_complete
            super().__init__(settings=STTSettings(model=model_size, language=None))
            self._model = WhisperModel(model_size, device=device, compute_type=compute_type)

        async def run_stt(self, audio: bytes) -> str:
            audio_np = np.frombuffer(audio, dtype=np.int16).astype(np.float32) / 32768.0
            segments, _ = self._model.transcribe(audio_np, language=None)
            return "".join(s.text for s in segments)

    return FasterWhisperSTTService(
        model_size=config.stt_model,
        device="cuda",
        compute_type="float16",
    )