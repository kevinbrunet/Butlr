from __future__ import annotations

import wave
from collections.abc import Callable
from pathlib import Path

import pytest


@pytest.fixture
def write_silent_wav() -> Callable[[Path, float, int, int], None]:
    def _write(
        path: Path,
        duration_s: float,
        sample_rate_hz: int = 16_000,
        sample_width_bytes: int = 2,
    ) -> None:
        n_frames = int(duration_s * sample_rate_hz)
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(sample_width_bytes)
            wav_file.setframerate(sample_rate_hz)
            wav_file.writeframes(b"\x00" * sample_width_bytes * n_frames)

    return _write
