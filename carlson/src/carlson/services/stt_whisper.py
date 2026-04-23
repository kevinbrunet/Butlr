"""faster-whisper STT service — wraps Pipecat's FastWhisperSTTService.

~ Le nom exact de la classe Pipecat peut varier selon la version :
  - pipecat >= 0.0.50 : FastWhisperSTTService dans pipecat.services.faster_whisper
  - Si l'import rate, faire : python -c "from pipecat.services import faster_whisper; dir(faster_whisper)"

Comportement attendu dans le pipeline :
  UserStartedSpeakingFrame → accumule les AudioRawFrame
  UserStoppedSpeakingFrame → transcrit le buffer → émet TranscriptionFrame
"""

from __future__ import annotations

import logging

from ..config import Config

log = logging.getLogger("carlson.stt")


def build_stt_service(config: Config):
    """Return a configured Pipecat STT service using faster-whisper.

    Whisper détecte automatiquement la langue (FR/EN) — on ne force pas
    `language` pour supporter le mode bilingue. Sur les énoncés très courts,
    la détection peut dériver ; ajuster `language` si besoin.

    ~ `device="cuda"` et `compute_type="float16"` supposent une GPU NVIDIA avec
    CUDA. Passer `device="cpu"` et `compute_type="int8"` si pas de GPU
    disponible (latence x4–x10 ~).
    """
    # ~ import path stable depuis pipecat 0.0.48
    from pipecat.services.faster_whisper import FastWhisperSTTService

    return FastWhisperSTTService(
        model=config.stt_model,
        device="cuda",
        compute_type="float16",
        # language=None → auto-detect FR/EN
    )
