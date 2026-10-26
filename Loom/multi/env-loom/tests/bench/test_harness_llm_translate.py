from __future__ import annotations

from loom_orchestrator.bench.harness_llm_translate import split_into_sentences


def test_split_into_sentences_splits_on_strong_punctuation() -> None:
    text = "First sentence. Second sentence! Third one?"
    assert split_into_sentences(text) == [
        "First sentence.",
        "Second sentence!",
        "Third one?",
    ]


def test_split_into_sentences_returns_whole_text_without_split_points() -> None:
    assert split_into_sentences("星期日的早晨，我揭去一张隔夜的日历") == [
        "星期日的早晨，我揭去一张隔夜的日历"
    ]


def test_split_into_sentences_strips_surrounding_whitespace() -> None:
    assert split_into_sentences("  One sentence.  ") == ["One sentence."]
