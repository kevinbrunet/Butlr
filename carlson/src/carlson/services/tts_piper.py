"""Piper TTS service — wraps Pipecat's PiperTTSService.

Piper est l'engine TTS par défaut (cf. ADR choisissant local-first).
Les voix sont des fichiers .onnx + .onnx.json téléchargeables séparément.

~ Chemin vers les modèles :
  - TTS_VOICE_FR / TTS_VOICE_EN dans .env peuvent être :
    (a) un nom court ("fr_FR-siwis-medium") si PiperTTSService sait résoudre
        les noms via le répertoire de téléchargement par défaut ~.
    (b) un chemin absolu vers le .onnx déjà téléchargé (recommandé ⚠ — le
        téléchargement automatique n'est pas garanti selon la version).
  - Pour télécharger manuellement :
      pip install piper-tts
      python -m piper.download fr_FR-siwis-medium  # emplacement ~/.local/share/piper ~

~ Si pipecat ne dispose pas de PiperTTSService dans la version installée,
  remplacer par le FrameProcessor custom ci-dessous (décommenté).

Phase 2 : une seule voix (FR). Commutation FR/EN à la phrase — Phase 4.
"""

from __future__ import annotations

import logging

from ..config import Config

log = logging.getLogger("carlson.tts")


def build_tts_service(config: Config):
    """Return a Pipecat TTS service using Piper (default voice = French).

    ~ PiperTTSService import path — stable depuis pipecat 0.0.48 ~.
    Si l'import rate, décommenter le FrameProcessor custom en bas de fichier.
    """
    from pipecat.services.piper.tts import PiperTTSService

    log.info("TTS service → piper, voix FR=%s", config.tts_voice_fr)
    return PiperTTSService(
        settings=PiperTTSService.Settings(voice=config.tts_voice_fr),
        use_cuda=True,
        # ~ sample_rate : Piper génère en 22050 Hz par défaut. Si LocalAudioTransport
        # attend une fréquence différente, ajuster ici ou dans les params transport.
    )


# ---------------------------------------------------------------------------
# Fallback : FrameProcessor custom si PiperTTSService n'est pas disponible.
# Décommenter build_tts_service_custom() et appeler depuis pipeline.py.
# ---------------------------------------------------------------------------

# def build_tts_service_custom(config: Config):
#     """Custom Piper TTS FrameProcessor — bypasse pipecat.services.piper."""
#     import io
#     import wave
#     import numpy as np
#     from piper import PiperVoice
#     from pipecat.frames.frames import AudioRawFrame, TextFrame
#     from pipecat.processors.frame_processor import FrameProcessor
#
#     voice = PiperVoice.load(config.tts_voice_fr)
#
#     class _PiperProcessor(FrameProcessor):
#         async def process_frame(self, frame, direction):
#             if isinstance(frame, TextFrame) and frame.text.strip():
#                 buf = io.BytesIO()
#                 with wave.open(buf, "wb") as wf:
#                     voice.synthesize(frame.text, wf)
#                 buf.seek(44)  # skip WAV header
#                 audio_bytes = buf.read()
#                 audio = np.frombuffer(audio_bytes, dtype=np.int16)
#                 # ~ sample_rate Piper par défaut ≈ 22050 Hz
#                 await self.push_frame(AudioRawFrame(audio.tobytes(), 22050, 1))
#             else:
#                 await self.push_frame(frame, direction)
#
#     return _PiperProcessor()
