from __future__ import annotations

import numpy as np

from loom_orchestrator.catch_up import (
    EDGE_PAUSE_S,
    INNER_PAUSE_S,
    MAX_RATE,
    CatchUp,
    rate_for_backlog,
    time_stretch,
    trim_silences,
)

SR = 1000


def _tone(seconds: float) -> np.ndarray:
    t = np.arange(int(seconds * SR)) / SR
    return (0.5 * np.sin(2 * np.pi * 50 * t)).astype(np.float32)


def _silence(seconds: float) -> np.ndarray:
    return np.zeros(int(seconds * SR), dtype=np.float32)


def test_trim_silences_shortens_edges_and_long_inner_pauses() -> None:
    audio = np.concatenate([_silence(1.0), _tone(0.5), _silence(1.0), _tone(0.5), _silence(1.0)])
    trimmed = trim_silences(audio, SR)
    expected = 2 * EDGE_PAUSE_S + 0.5 + INNER_PAUSE_S + 0.5
    assert abs(len(trimmed) / SR - expected) < 0.05


def test_trim_silences_keeps_short_inner_pauses() -> None:
    audio = np.concatenate([_tone(0.5), _silence(0.1), _tone(0.5)])
    assert len(trim_silences(audio, SR)) == len(audio)


def test_trim_silences_returns_pure_silence_unchanged() -> None:
    audio = _silence(1.0)
    assert len(trim_silences(audio, SR)) == len(audio)


def test_rate_for_backlog_ramps_up_to_max_rate() -> None:
    assert rate_for_backlog(0.0) == 1.0
    assert 1.0 < rate_for_backlog(3.0) < MAX_RATE
    assert rate_for_backlog(100.0) == MAX_RATE


def test_time_stretch_shortens_by_rate() -> None:
    audio = _tone(2.0)
    out = time_stretch(audio, 1.25, SR)
    assert len(out) == int(len(audio) / 1.25)


def test_catch_up_leaves_audio_untouched_when_not_late() -> None:
    audio = np.concatenate([_silence(1.0), _tone(0.5)])
    out = CatchUp(SR).process([audio], backlog_s=0.0)
    assert np.array_equal(out, audio)


def test_catch_up_cuts_silence_then_speeds_up_when_far_behind() -> None:
    catch_up = CatchUp(SR)
    audio = np.concatenate([_silence(1.0), _tone(2.0), _silence(1.0)])
    out = catch_up.process([audio[:1500], audio[1500:]], backlog_s=20.0)
    assert catch_up.silence_cut_s > 1.5
    assert catch_up.max_rate == MAX_RATE
    assert len(out) < (2.0 + 2 * EDGE_PAUSE_S) * SR
