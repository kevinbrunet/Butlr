"""Tests pour WakeWordProcessor.

On mocke openwakeword.model.Model pour tester la machine d'états sans dépendance GPU.
Les tests couvrent :
  - Le gate reste fermé sous le seuil.
  - La confirmation douce (N chunks consécutifs requis).
  - Le gate s'ouvre après confirmation et émet VADUserStartedSpeakingFrame.
  - Le gate se referme sur VADUserStoppedSpeakingFrame.
  - Les frames non-audio se propagent toujours (StartFrame, etc.).
  - Les VADUserStartedSpeakingFrame de Silero sont supprimés en état active.
"""

from __future__ import annotations

import asyncio
from typing import Any
from unittest.mock import MagicMock

import numpy as np
import pytest

from carlson.services.wake_word import WakeWordProcessor, _CONFIRMATION_CHUNKS, _CHUNK_SAMPLES


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_audio_frame(n_chunks: int = 1) -> Any:
    """Crée un AudioRawFrame factice avec n_chunks * _CHUNK_SAMPLES samples int16."""
    from pipecat.frames.frames import AudioRawFrame

    samples = n_chunks * _CHUNK_SAMPLES
    audio = (np.zeros(samples, dtype=np.int16)).tobytes()
    return AudioRawFrame(audio=audio, sample_rate=16000, num_channels=1)


def _mock_model(score: float) -> MagicMock:
    """Retourne un mock dont predict() retourne toujours {model: score}."""
    model = MagicMock()
    model.predict.return_value = {"hey_carlson": score}
    return model


class CapturingProcessor:
    """Collecte les frames poussées par le processeur sous test."""

    def __init__(self) -> None:
        self.frames: list[Any] = []

    async def push_frame(self, frame: Any, direction: Any = None) -> None:
        self.frames.append(frame)


async def _run(processor: WakeWordProcessor, *frames: Any) -> list[Any]:
    """Injecte les frames dans processor et retourne celles qu'il a poussées en aval."""
    from pipecat.processors.frame_processor import FrameDirection

    collector = CapturingProcessor()
    processor.push_frame = collector.push_frame  # type: ignore[method-assign]

    for frame in frames:
        await processor.process_frame(frame, FrameDirection.DOWNSTREAM)

    return collector.frames


def run(coro: Any) -> Any:
    """Wrapper synchrone pour exécuter une coroutine dans les tests."""
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

def test_audio_blocked_below_threshold() -> None:
    from pipecat.frames.frames import AudioRawFrame

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.1), threshold=0.5)
        pushed = await _run(proc, _make_audio_frame(1))
        audio_frames = [f for f in pushed if isinstance(f, AudioRawFrame)]
        assert not audio_frames, "L'audio ne doit pas passer en dessous du seuil"

    run(_test())


def test_no_trigger_on_single_chunk_above_threshold() -> None:
    """_CONFIRMATION_CHUNKS > 1 — un seul chunk ne doit pas déclencher."""
    from pipecat.frames.frames import VADUserStartedSpeakingFrame

    if _CONFIRMATION_CHUNKS <= 1:
        pytest.skip("_CONFIRMATION_CHUNKS=1 : confirmation non testable")

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.9), threshold=0.5)
        pushed = await _run(proc, _make_audio_frame(1))
        started = [f for f in pushed if isinstance(f, VADUserStartedSpeakingFrame)]
        assert not started, "Un seul chunk ne doit pas ouvrir la session"

    run(_test())


def test_trigger_after_confirmation() -> None:
    """N chunks consécutifs au-dessus du seuil ouvrent la session."""
    from pipecat.frames.frames import VADUserStartedSpeakingFrame

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.9), threshold=0.5)
        pushed = await _run(proc, _make_audio_frame(_CONFIRMATION_CHUNKS))
        started = [f for f in pushed if isinstance(f, VADUserStartedSpeakingFrame)]
        assert len(started) == 1, f"Attendu 1 VADUserStartedSpeakingFrame, obtenu {len(started)}"

    run(_test())


def test_confirmation_resets_on_low_score_chunk() -> None:
    """Un chunk sous le seuil au milieu réinitialise le compteur de confirmation."""
    from pipecat.frames.frames import VADUserStartedSpeakingFrame

    if _CONFIRMATION_CHUNKS < 2:
        pytest.skip("_CONFIRMATION_CHUNKS<2 : réinitialisation non testable")

    async def _test() -> None:
        model = MagicMock()
        call_count = 0

        def side_effect(samples):  # noqa: ANN001
            nonlocal call_count
            call_count += 1
            if call_count == 2:
                return {"hey_carlson": 0.1}  # chunk creux au milieu
            return {"hey_carlson": 0.9}

        model.predict.side_effect = side_effect

        proc = WakeWordProcessor(model=model, threshold=0.5)
        # _CONFIRMATION_CHUNKS+1 chunks : confirmation ne doit pas aboutir (réinitialisation)
        pushed = await _run(proc, _make_audio_frame(_CONFIRMATION_CHUNKS + 1))
        started = [f for f in pushed if isinstance(f, VADUserStartedSpeakingFrame)]
        assert not started, "La confirmation doit être réinitialisée par un chunk sous le seuil"

    run(_test())


def test_session_closes_on_stopped_speaking() -> None:
    """En état active, VADUserStoppedSpeakingFrame ferme la session."""
    from pipecat.frames.frames import AudioRawFrame, VADUserStoppedSpeakingFrame

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.9), threshold=0.5)

        # Ouvre la session
        await _run(proc, _make_audio_frame(_CONFIRMATION_CHUNKS))
        assert proc._active

        # Simule fin de tour puis nouvel audio
        pushed = await _run(
            proc,
            VADUserStoppedSpeakingFrame(),
            _make_audio_frame(1),
        )
        assert not proc._active, "La session doit être fermée après StoppedSpeaking"

        audio_after = [f for f in pushed if isinstance(f, AudioRawFrame)]
        assert not audio_after, "L'audio post-fermeture ne doit pas passer"

    run(_test())


def test_silero_started_suppressed_in_active_state() -> None:
    """En état active, VADUserStartedSpeakingFrame de Silero est supprimé."""
    from pipecat.frames.frames import VADUserStartedSpeakingFrame

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.9), threshold=0.5)
        # Ouvre la session (1 VADUserStartedSpeakingFrame émis par nous)
        await _run(proc, _make_audio_frame(_CONFIRMATION_CHUNKS))
        assert proc._active

        # Silero envoie un Started pendant la session
        extra = await _run(proc, VADUserStartedSpeakingFrame())
        silero_started = [f for f in extra if isinstance(f, VADUserStartedSpeakingFrame)]
        assert not silero_started, "Le Started de Silero doit être supprimé en état active"

    run(_test())


def test_non_audio_frames_always_propagate() -> None:
    """Les frames qui ne sont pas audio/VAD se propagent toujours en état sleeping.

    Note : StartFrame/EndFrame déclenchent le lifecycle interne de Pipecat (TaskManager)
    et ne peuvent pas être testés hors pipeline complet. On utilise TextFrame à la place,
    qui représente n'importe quel flux de données non-audio.
    """
    from pipecat.frames.frames import TextFrame

    async def _test() -> None:
        proc = WakeWordProcessor(model=_mock_model(0.0), threshold=0.5)
        text_frame = TextFrame(text="bonjour")
        pushed = await _run(proc, text_frame)
        assert text_frame in pushed, "TextFrame doit toujours passer en état sleeping"

    run(_test())
