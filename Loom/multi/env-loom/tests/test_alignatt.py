from __future__ import annotations

from loom_orchestrator.alignatt import compute_increment, safe_token_count


def test_safe_token_count_all_safe_when_far_from_edge() -> None:
    # encoder_seq_len=100, frontier_frames=5 -> frontier=95 ; tous < 95
    assert safe_token_count([10, 20, 30], encoder_seq_len=100, frontier_frames=5) == 3


def test_safe_token_count_stops_at_first_unsafe_token() -> None:
    # frontier=95 ; le 2e token (98) est au-delà -> seul le 1er est sûr
    assert safe_token_count([10, 98, 20], encoder_seq_len=100, frontier_frames=5) == 1


def test_safe_token_count_does_not_resume_after_unsafe_token() -> None:
    # un token sûr après un token incertain ne compte pas (pas de retour en arrière)
    assert safe_token_count([98, 10], encoder_seq_len=100, frontier_frames=5) == 0


def test_safe_token_count_empty_input() -> None:
    assert safe_token_count([], encoder_seq_len=100, frontier_frames=5) == 0


def test_safe_token_count_boundary_frame_is_unsafe() -> None:
    # frame == frontier exactement -> pas < frontier -> incertain
    assert safe_token_count([95], encoder_seq_len=100, frontier_frames=5) == 0


def test_compute_increment_extends_committed_prefix() -> None:
    increment, is_consistent = compute_increment("Bonjour", "Bonjour le monde")
    assert increment == " le monde"
    assert is_consistent is True


def test_compute_increment_no_new_text() -> None:
    increment, is_consistent = compute_increment("Bonjour", "Bonjour")
    assert increment == ""
    assert is_consistent is True


def test_compute_increment_empty_committed() -> None:
    increment, is_consistent = compute_increment("", "Bonjour")
    assert increment == "Bonjour"
    assert is_consistent is True


def test_compute_increment_flags_inconsistent_revision() -> None:
    increment, is_consistent = compute_increment("Bonjour le monde", "Bonjour tout le monde")
    assert increment == ""
    assert is_consistent is False
