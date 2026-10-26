from __future__ import annotations

from loom_orchestrator.bench.evaluate import diff_text, first_output_latency_s, format_evaluation


def test_diff_text_identical_returns_ratio_one() -> None:
    diff, ratio = diff_text("bonjour le monde", "bonjour le monde")
    assert diff == "bonjour le monde"
    assert ratio == 1.0


def test_diff_text_marks_deletion_and_insertion() -> None:
    diff, ratio = diff_text("bonjour le monde", "bonjour le vaste monde")
    assert diff == "bonjour le +vaste monde"
    assert ratio < 1.0


def test_diff_text_marks_replace() -> None:
    diff, _ratio = diff_text("bonjour le monde", "salut le monde")
    assert "-bonjour" in diff
    assert "+salut" in diff


def test_diff_text_empty_strings() -> None:
    diff, ratio = diff_text("", "")
    assert diff == ""
    assert ratio == 1.0


def test_first_output_latency_s_returns_earliest_tts_t_out() -> None:
    events = [
        {"stage": "wlk", "t_out": 0.1},
        {"stage": "tts", "t_out": 2.5},
        {"stage": "tts", "t_out": 1.2},
        {"stage": "seamless", "t_out": 0.8},
    ]
    assert first_output_latency_s(events) == 1.2


def test_first_output_latency_s_returns_none_when_no_tts_events() -> None:
    events = [{"stage": "wlk", "t_out": 0.1}]
    assert first_output_latency_s(events) is None


def test_first_output_latency_s_empty_events() -> None:
    assert first_output_latency_s([]) is None


def test_format_evaluation_includes_all_fields() -> None:
    report = format_evaluation(
        corpus_key="a",
        provenance="source X",
        ratio=0.5,
        diff="-bonjour +salut",
        latency_s=1.234,
        audio_dir="bench-runs/a-audio",
        transcript_path="bench-runs/a.transcript.txt",
        log_path="bench-runs/a.jsonl",
    )
    assert "corpus a" in report
    assert "source X" in report
    assert "50.0%" in report
    assert "1.23s" in report
    assert "-bonjour +salut" in report
    assert "bench-runs/a-audio" in report


def test_format_evaluation_handles_no_output() -> None:
    report = format_evaluation(
        corpus_key="a",
        provenance="source X",
        ratio=0.0,
        diff="",
        latency_s=None,
        audio_dir="bench-runs/a-audio",
        transcript_path="bench-runs/a.transcript.txt",
        log_path="bench-runs/a.jsonl",
    )
    assert "aucun audio produit" in report
