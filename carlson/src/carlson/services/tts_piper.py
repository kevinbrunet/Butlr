"""Piper TTS service wiring.

Piper models are language-specific. We ship two voices (FR, EN) and switch at
the sentence level based on the LLM's output language.
"""

from __future__ import annotations

from ..config import Config


def build_tts_service(config: Config):
    """Return a Pipecat TTS service using Piper.

    Switching voices mid-conversation: Pipecat TTS services usually expose a
    `set_voice()` method. ~ exact API depends on version. If not available,
    route FR and EN frames to two separate TTS processors behind a lang router.
    """
    # from pipecat.services.piper import PiperTTSService
    # return PiperTTSService(voice=config.tts_voice_fr, ...)
    raise NotImplementedError("Pipecat Piper wiring — pin the SDK version first.")
