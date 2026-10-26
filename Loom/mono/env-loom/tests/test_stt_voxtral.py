from __future__ import annotations

from loom_orchestrator.bench.timestamps import hms_to_seconds
from loom_orchestrator.stt_voxtral import SentenceLines, seconds_to_hms


def test_seconds_to_hms_round_trips_with_wlk_parser() -> None:
    assert hms_to_seconds(seconds_to_hms(3725.37)) == 3725.37


def test_seconds_to_hms_clamps_negative_estimates() -> None:
    assert seconds_to_hms(-0.4) == "0:00:00.00"


def test_active_line_grows_until_sentence_end() -> None:
    lines = SentenceLines()
    lines.append("Hello every", 1.0)
    lines.append("one", 1.2)
    assert [x["text"] for x in lines.lines()] == ["Hello everyone"]


def test_sentence_end_followed_by_space_seals_line() -> None:
    lines = SentenceLines()
    lines.append("Hello everyone. Today we", 2.0)
    out = lines.lines()
    assert [x["text"] for x in out] == ["Hello everyone.", "Today we"]
    assert lines.sealed[0]["text"] == "Hello everyone."


def test_period_without_following_space_does_not_seal_yet() -> None:
    lines = SentenceLines()
    lines.append("It costs 3.", 1.0)
    assert lines.sealed == []
    lines.append("5 euros", 1.5)
    assert [x["text"] for x in lines.lines()] == ["It costs 3.5 euros"]


def test_several_sentences_in_one_delta() -> None:
    lines = SentenceLines()
    lines.append("One. Two! Three? Fo", 3.0)
    assert [x["text"] for x in lines.lines()] == ["One.", "Two!", "Three?", "Fo"]


def test_sealed_lines_never_change() -> None:
    lines = SentenceLines()
    lines.append("First sentence. Sec", 1.0)
    first = dict(lines.sealed[0])
    lines.append("ond sentence. Third", 2.0)
    assert lines.sealed[0] == first
    assert [x["text"] for x in lines.lines()] == ["First sentence.", "Second sentence.", "Third"]


def test_seal_active_on_pause() -> None:
    lines = SentenceLines()
    lines.append("no punctuation here", 4.0)
    assert lines.seal_active() is True
    assert lines.lines() == [{"text": "no punctuation here", "end": "0:00:04.00", "speaker": "1"}]
    assert lines.seal_active() is False


def test_text_after_pause_starts_a_new_line() -> None:
    lines = SentenceLines()
    lines.append("first part", 1.0)
    lines.seal_active()
    lines.append(" second part", 3.0)
    assert [x["text"] for x in lines.lines()] == ["first part", "second part"]


def test_speaker_label_is_carried() -> None:
    lines = SentenceLines(speaker="2")
    lines.append("hi", 0.5)
    assert lines.lines()[0]["speaker"] == "2"


def test_long_unpunctuated_line_is_sealed_at_the_last_space() -> None:
    lines = SentenceLines(max_words=4)
    lines.append("one two three fo", 1.0)
    assert lines.sealed == []
    lines.append("ur fi", 1.2)  # 5 mots : « fi » peut encore grandir, il reste actif
    assert [x["text"] for x in lines.sealed] == ["one two three four"]
    lines.append("ve six", 1.4)
    assert [x["text"] for x in lines.lines()] == ["one two three four", "five six"]


def test_leading_space_tokens_still_trigger_the_cut() -> None:
    lines = SentenceLines(max_words=3)
    for token in ["one", " two", " three", " four", " five"]:
        lines.append(token, 1.0)
    assert lines.sealed[0]["text"] == "one two three"


def test_short_unpunctuated_line_stays_active() -> None:
    lines = SentenceLines(max_words=4)
    lines.append("one two three ", 1.0)
    assert lines.sealed == []


def test_rotation_waits_for_a_pause_after_rotate_s() -> None:
    from loom_orchestrator.stt_voxtral import should_rotate

    assert should_rotate(19.9, since_text_s=5.0) is False
    assert should_rotate(20.0, since_text_s=0.1) is False  # on parle : on attend la pause
    assert should_rotate(20.0, since_text_s=0.6) is True


def test_rotation_is_forced_at_rotate_max_s() -> None:
    from loom_orchestrator.stt_voxtral import should_rotate

    assert should_rotate(30.0, since_text_s=0.0) is True
