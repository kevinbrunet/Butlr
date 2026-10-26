from __future__ import annotations

from loom_orchestrator.bench.timestamps import hms_to_seconds


def test_hms_to_seconds_zero() -> None:
    assert hms_to_seconds("0:00:00") == 0.0


def test_hms_to_seconds_simple() -> None:
    assert hms_to_seconds("0:00:03") == 3.0


def test_hms_to_seconds_with_minutes_and_hours() -> None:
    assert hms_to_seconds("1:02:03") == 3723.0


def test_hms_to_seconds_centisecond_precision() -> None:
    assert hms_to_seconds("0:00:03.45") == 3.45
