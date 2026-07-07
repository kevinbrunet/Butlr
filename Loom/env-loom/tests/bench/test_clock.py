from __future__ import annotations

import pytest

from loom_orchestrator.bench.clock import SampleClock


def test_elapsed_seconds_at_zero_is_zero() -> None:
    clock = SampleClock(sample_rate_hz=16_000)
    assert clock.elapsed_seconds(0) == 0.0


def test_elapsed_seconds_one_second_of_samples() -> None:
    clock = SampleClock(sample_rate_hz=16_000)
    assert clock.elapsed_seconds(16_000) == 1.0


def test_sample_index_round_trips_elapsed_seconds() -> None:
    clock = SampleClock(sample_rate_hz=16_000)
    assert clock.sample_index(clock.elapsed_seconds(32_000)) == 32_000


def test_elapsed_seconds_rejects_negative_sample_index() -> None:
    clock = SampleClock(sample_rate_hz=16_000)
    with pytest.raises(ValueError):
        clock.elapsed_seconds(-1)


def test_sample_index_rejects_negative_elapsed_seconds() -> None:
    clock = SampleClock(sample_rate_hz=16_000)
    with pytest.raises(ValueError):
        clock.sample_index(-0.5)
