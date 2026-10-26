from __future__ import annotations

from loom_orchestrator.bench.line_tracking import extract_updates


def test_extract_updates_reports_new_line() -> None:
    known_texts: list[str] = []
    lines = [{"speaker": 1, "text": "hello", "end": "0:00:01.00"}]

    updates = extract_updates(lines, known_texts)

    assert len(updates) == 1
    idx, line, text = updates[0]
    assert idx == 0
    assert text == "hello"
    assert known_texts == ["hello"]


def test_extract_updates_reports_growth_of_existing_line() -> None:
    known_texts = ["hello"]
    lines = [{"speaker": 1, "text": "hello world", "end": "0:00:02.00"}]

    updates = extract_updates(lines, known_texts)

    assert len(updates) == 1
    _, _, text = updates[0]
    assert text == "hello world"
    assert known_texts == ["hello world"]


def test_extract_updates_returns_nothing_when_text_unchanged() -> None:
    known_texts = ["hello world"]
    lines = [{"speaker": 1, "text": "hello world", "end": "0:00:02.00"}]

    updates = extract_updates(lines, known_texts)

    assert updates == []
    assert known_texts == ["hello world"]


def test_extract_updates_handles_multiple_lines_independently() -> None:
    known_texts = ["hello world"]
    lines = [
        {"speaker": 1, "text": "hello world", "end": "0:00:02.00"},
        {"speaker": 2, "text": "second line", "end": "0:00:05.00"},
    ]

    updates = extract_updates(lines, known_texts)

    assert len(updates) == 1
    idx, _, text = updates[0]
    assert idx == 1
    assert text == "second line"
    assert known_texts == ["hello world", "second line"]


def test_extract_updates_ignores_empty_text() -> None:
    known_texts: list[str] = []
    lines = [{"speaker": -2, "text": None, "end": "0:00:01.00"}]

    updates = extract_updates(lines, known_texts)

    assert updates == []
    assert known_texts == [""]


def test_extract_updates_prefers_translation_over_text() -> None:
    known_texts: list[str] = []
    lines = [{"speaker": 1, "text": "hello", "translation": "bonjour", "end": "0:00:01.00"}]

    updates = extract_updates(lines, known_texts)

    assert len(updates) == 1
    _, _, text = updates[0]
    assert text == "bonjour"
