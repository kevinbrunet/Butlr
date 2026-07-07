from __future__ import annotations

from loom_orchestrator.commit_policy import compute_flush, find_last_boundary


def test_find_last_boundary_finds_sentence_end() -> None:
    assert find_last_boundary("Hello there. And") == 12


def test_find_last_boundary_finds_comma() -> None:
    assert find_last_boundary("Hello there, and") == 12


def test_find_last_boundary_prefers_last_over_first() -> None:
    assert find_last_boundary("One. Two. Three") == 9


def test_find_last_boundary_none_when_no_punctuation() -> None:
    assert find_last_boundary("no punctuation here") is None


def test_compute_flush_emits_segment_up_to_sentence_end() -> None:
    segment, new_flushed, is_consistent = compute_flush("First sentence. And more", "")
    assert segment == "First sentence."
    assert new_flushed == "First sentence."
    assert is_consistent is True


def test_compute_flush_nothing_new_without_boundary() -> None:
    segment, new_flushed, is_consistent = compute_flush("First sentence", "")
    assert segment == ""
    assert new_flushed == ""
    assert is_consistent is True


def test_compute_flush_only_returns_unflushed_part() -> None:
    segment, new_flushed, is_consistent = compute_flush(
        "First sentence. Second one, still going", "First sentence."
    )
    assert segment == "Second one,"
    assert new_flushed == "First sentence. Second one,"
    assert is_consistent is True


def test_compute_flush_flags_inconsistent_revision() -> None:
    segment, new_flushed, is_consistent = compute_flush(
        "Totally different text.", "First sentence."
    )
    assert segment == ""
    assert new_flushed == "First sentence."
    assert is_consistent is False


def test_compute_flush_empty_already_flushed_and_no_boundary_yet() -> None:
    segment, new_flushed, is_consistent = compute_flush("", "")
    assert segment == ""
    assert new_flushed == ""
    assert is_consistent is True
