"""Wake word service — openWakeWord wrapped as a Pipecat FrameProcessor.

Pipeline position : entre transport.input() et STT.

Machine d'états :
    sleeping   — openWakeWord tourne sur l'audio mic ; rien ne passe en aval.
    confirming — N chunks consécutifs au-dessus du seuil requis (étape 4.3).
    active     — session ouverte ; tout passe jusqu'au VADUserStoppedSpeakingFrame.
"""

from __future__ import annotations

import logging
from typing import Any

import numpy as np
from pipecat.frames.frames import (
    AudioRawFrame,
    Frame,
    VADUserStartedSpeakingFrame,
    VADUserStoppedSpeakingFrame,
)
from pipecat.processors.frame_processor import FrameDirection, FrameProcessor

from ..config import Config

log = logging.getLogger("carlson.wake_word")

# ~ openWakeWord traite l'audio en chunks de 80 ms à 16 kHz → 1280 samples int16.
_CHUNK_SAMPLES = 1280

# Étape 4.3 — confirmation douce : N chunks consécutifs ≥ threshold pour valider.
# ~160 ms de confirmation supplémentaire, latence imperceptible (<200 ms).
_CONFIRMATION_CHUNKS = 2


class WakeWordProcessor(FrameProcessor):
    """Gate les frames audio/VAD jusqu'à détection du wake word.

    En état sleeping : l'audio est consommé par openWakeWord mais bloqué en aval.
    Les VADUser*SpeakingFrame de Silero sont aussi supprimés (évite des tours STT
    intempestifs avant la phrase magique).

    En état active : tout passe. On supprime cependant tout nouveau
    VADUserStartedSpeakingFrame de Silero (on en a déjà émis un nous-mêmes).
    Sur VADUserStoppedSpeakingFrame : on repasse en sleeping.
    """

    def __init__(self, model: Any, threshold: float) -> None:
        super().__init__()
        self._model = model
        self._threshold = threshold
        self._active = False
        self._audio_buf: bytes = b""
        self._confirm_count = 0

    async def process_frame(self, frame: Frame, direction: FrameDirection) -> None:
        await super().process_frame(frame, direction)

        # Les frames de contrôle (Start, End, Cancel, System…) se propagent toujours.
        if not isinstance(frame, (AudioRawFrame, VADUserStartedSpeakingFrame, VADUserStoppedSpeakingFrame)):
            await self.push_frame(frame, direction)
            return

        if self._active:
            if isinstance(frame, VADUserStoppedSpeakingFrame):
                log.debug("Session wake word fermée — retour en veille")
                self._active = False
                self._audio_buf = b""
                self._confirm_count = 0
                await self.push_frame(frame, direction)
            elif isinstance(frame, VADUserStartedSpeakingFrame):
                # Silero peut émettre un Started alors qu'on vient d'en envoyer un ;
                # on le supprime pour éviter un double-trigger sur le STT.
                pass
            else:
                await self.push_frame(frame, direction)
            return

        # État sleeping : alimenter openWakeWord, tout bloquer en aval.
        if isinstance(frame, AudioRawFrame):
            self._audio_buf += frame.audio
            await self._process_chunks()
        # VADUser*SpeakingFrame de Silero : supprimés en sleeping.

    async def _process_chunks(self) -> None:
        bytes_per_chunk = _CHUNK_SAMPLES * 2  # int16 = 2 octets/sample
        while len(self._audio_buf) >= bytes_per_chunk:
            chunk = self._audio_buf[:bytes_per_chunk]
            self._audio_buf = self._audio_buf[bytes_per_chunk:]

            samples = np.frombuffer(chunk, dtype=np.int16).astype(np.float32) / 32768.0
            # ~ Model.predict retourne un dict {model_name: float} — API openWakeWord>=0.6.
            scores: dict[str, float] = self._model.predict(samples)
            score = max(scores.values()) if scores else 0.0

            if score >= self._threshold:
                self._confirm_count += 1
                log.debug(
                    "Wake word score=%.3f — confirmation %d/%d",
                    score,
                    self._confirm_count,
                    _CONFIRMATION_CHUNKS,
                )
                if self._confirm_count >= _CONFIRMATION_CHUNKS:
                    log.info("Wake word détecté — session ouverte (score=%.3f)", score)
                    self._active = True
                    self._confirm_count = 0
                    await self.push_frame(VADUserStartedSpeakingFrame())
            else:
                # Réinitialise la fenêtre de confirmation sur tout chunk sous le seuil.
                if self._confirm_count > 0:
                    log.debug("Confirmation réinitialisée (score=%.3f < %.2f)", score, self._threshold)
                self._confirm_count = 0


def build_wake_word_service(config: Config) -> WakeWordProcessor:
    # ~ Import conditionnel : openwakeword n'est requis que si USE_WAKEWORD=1.
    from openwakeword.model import Model  # type: ignore[import-untyped]

    model = Model(
        wakeword_models=[config.wakeword_model],
        inference_framework="tflite",
    )
    log.info(
        "Wake word model chargé : %s (seuil=%.2f, confirmation=%d chunks)",
        config.wakeword_model,
        config.wakeword_threshold,
        _CONFIRMATION_CHUNKS,
    )
    return WakeWordProcessor(model=model, threshold=config.wakeword_threshold)
