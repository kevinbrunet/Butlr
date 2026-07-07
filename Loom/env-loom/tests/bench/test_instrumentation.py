from __future__ import annotations

import json

import pytest

from loom_orchestrator.bench.instrumentation import (
    BUDGET_MS,
    STAGE_TTS,
    STAGE_WLK,
    EventLogger,
    LatencyEvent,
)


def test_create_within_budget_is_not_exceeded() -> None:
    event = LatencyEvent.create("seg-1", STAGE_TTS, t_in=0.0, t_out=0.1)
    assert event.exceeded is False
    assert event.budget_ms == BUDGET_MS[STAGE_TTS]


def test_create_over_budget_is_exceeded() -> None:
    event = LatencyEvent.create("seg-1", STAGE_WLK, t_in=0.0, t_out=1.5)
    assert event.exceeded is True


def test_create_rejects_unknown_stage() -> None:
    with pytest.raises(ValueError):
        LatencyEvent.create("seg-1", "inconnu", t_in=0.0, t_out=0.1)


def test_create_rejects_t_out_before_t_in() -> None:
    with pytest.raises(ValueError):
        LatencyEvent.create("seg-1", STAGE_WLK, t_in=1.0, t_out=0.5)


def test_event_logger_writes_one_json_line_per_event(tmp_path) -> None:
    log_path = tmp_path / "run" / "events.jsonl"
    events = [
        LatencyEvent.create("seg-1", STAGE_WLK, 0.0, 0.5),
        LatencyEvent.create("seg-2", STAGE_TTS, 0.5, 0.7),
    ]

    with EventLogger(log_path) as logger:
        for event in events:
            logger.log(event)

    lines = log_path.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 2
    assert json.loads(lines[0])["segment_id"] == "seg-1"
    assert json.loads(lines[1])["stage"] == STAGE_TTS
