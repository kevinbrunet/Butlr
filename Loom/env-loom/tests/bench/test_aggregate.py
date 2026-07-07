from __future__ import annotations

import json

from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    load_events,
)
from loom_orchestrator.bench.instrumentation import STAGE_TTS, STAGE_WLK, LatencyEvent


def _as_dict(event: LatencyEvent) -> dict:
    return {
        "segment_id": event.segment_id,
        "stage": event.stage,
        "t_in": event.t_in,
        "t_out": event.t_out,
        "budget_ms": event.budget_ms,
        "exceeded": event.exceeded,
    }


def test_load_events_reads_jsonl(tmp_path) -> None:
    path = tmp_path / "events.jsonl"
    event = LatencyEvent.create("seg-1", STAGE_WLK, 0.0, 0.5)
    path.write_text(json.dumps(_as_dict(event)) + "\n", encoding="utf-8")

    events = load_events(path)

    assert events == [_as_dict(event)]


def test_aggregate_by_stage_computes_p50_p95_per_stage() -> None:
    events = [
        _as_dict(LatencyEvent.create(f"seg-{i}", STAGE_WLK, 0.0, duration))
        for i, duration in enumerate([0.1, 0.2, 0.3, 0.4, 1.2])
    ]

    reports = aggregate_by_stage(events)

    assert len(reports) == 1
    report = reports[0]
    assert report.stage == STAGE_WLK
    assert report.count == 5
    assert report.exceeded_count == 1  # seul 1.2s (1200ms) dépasse le budget WLK (1000ms)
    assert report.p50_ms <= report.p95_ms


def test_aggregate_end_to_end_spans_first_t_in_to_last_t_out() -> None:
    events = [
        _as_dict(LatencyEvent.create("seg-1", STAGE_WLK, 0.0, 0.8)),
        _as_dict(LatencyEvent.create("seg-1", STAGE_TTS, 0.8, 1.0)),
    ]

    report = aggregate_end_to_end(events)

    assert report is not None
    assert report.count == 1
    assert report.p50_ms == 1000.0  # 0.0 -> 1.0s de bout en bout


def test_aggregate_end_to_end_returns_none_when_no_events() -> None:
    assert aggregate_end_to_end([]) is None
