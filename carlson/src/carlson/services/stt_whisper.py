"""faster-whisper STT service wiring.

Pipecat has an existing Whisper integration; the concrete class name depends on
the installed version. ~ adjust imports at pin time.
"""

from __future__ import annotations

from ..config import Config


def build_stt_service(config: Config):
    """Return a Pipecat STT service configured for bilingual FR/EN.

    Whisper is multilingual out of the box — we don't force a language so it
    auto-detects per turn. Downside: occasional misdetections on very short
    utterances. Mitigation: a short lang-detect post-pass on low-confidence
    transcripts.
    """
    # from pipecat.services.whisper import WhisperSTTService
    # return WhisperSTTService(model=config.stt_model, device="cuda")
    raise NotImplementedError("Pipecat STT wiring — pin the SDK version first.")
