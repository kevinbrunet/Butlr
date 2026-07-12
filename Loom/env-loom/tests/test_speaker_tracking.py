from __future__ import annotations

from loom_orchestrator.speaker_tracking import (
    assign_streams_open_set,
    cosine_similarity,
    find_best_speaker,
    pick_matching_stream,
    streams_are_distinct,
    update_running_embedding,
)


def test_cosine_similarity_identical_vectors() -> None:
    assert cosine_similarity([1.0, 2.0, 3.0], [1.0, 2.0, 3.0]) == 1.0


def test_cosine_similarity_orthogonal_vectors() -> None:
    assert cosine_similarity([1.0, 0.0], [0.0, 1.0]) == 0.0


def test_cosine_similarity_opposite_vectors() -> None:
    assert cosine_similarity([1.0, 0.0], [-1.0, 0.0]) == -1.0


def test_cosine_similarity_zero_vector_returns_zero() -> None:
    assert cosine_similarity([0.0, 0.0], [1.0, 2.0]) == 0.0


def test_streams_are_distinct_true_for_different_embeddings() -> None:
    a = [1.0, 0.0]
    b = [0.0, 1.0]
    assert streams_are_distinct([a, b]) is True


def test_streams_are_distinct_false_for_near_identical_embeddings() -> None:
    a = [1.0, 0.0]
    b = [0.99, 0.01]
    assert streams_are_distinct([a, b]) is False


def test_streams_are_distinct_false_with_fewer_than_two_streams() -> None:
    assert streams_are_distinct([[1.0, 0.0]]) is False
    assert streams_are_distinct([]) is False


def test_pick_matching_stream_returns_closest_index() -> None:
    reference = [1.0, 0.0]
    streams = [[0.0, 1.0], [0.9, 0.1]]
    index, similarity = pick_matching_stream(reference, streams)
    assert index == 1
    assert similarity > 0.9


def test_update_running_embedding_no_prior_returns_new() -> None:
    assert update_running_embedding(None, [1.0, 2.0], count=0) == [1.0, 2.0]


def test_update_running_embedding_averages_with_prior() -> None:
    old = [0.0, 0.0]
    new = [2.0, 4.0]
    result = update_running_embedding(old, new, count=1)
    assert result == [1.0, 2.0]


def test_update_running_embedding_weights_by_count() -> None:
    old = [10.0]
    new = [0.0]
    result = update_running_embedding(old, new, count=9)
    assert result == [9.0]


def test_find_best_speaker_no_known_speakers_returns_none() -> None:
    index, similarity = find_best_speaker([], [1.0, 0.0])
    assert index is None
    assert similarity == 0.0


def test_find_best_speaker_matches_closest_above_threshold() -> None:
    id0, id1 = [1.0, 0.0], [0.0, 1.0]
    index, similarity = find_best_speaker([id0, id1], [0.05, 0.95])
    assert index == 1
    assert similarity > 0.9


def test_find_best_speaker_returns_none_below_threshold() -> None:
    id0 = [1.0, 0.0]
    index, similarity = find_best_speaker([id0], [0.0, 1.0], threshold=0.5)
    assert index is None
    assert similarity == 0.0


def test_find_best_speaker_respects_custom_threshold() -> None:
    id0 = [1.0, 0.0]
    borderline = [0.6, 0.5]
    index, _similarity = find_best_speaker([id0], borderline, threshold=0.9)
    assert index is None
    index, _similarity = find_best_speaker([id0], borderline, threshold=0.5)
    assert index == 0


def test_assign_streams_open_set_no_known_speakers_registers_all_as_new() -> None:
    stream0, stream1 = [0.9, 0.1], [0.1, 0.9]
    assert assign_streams_open_set([], [stream0, stream1]) == [0, 1]


def test_assign_streams_open_set_matches_existing_straight() -> None:
    id0, id1 = [1.0, 0.0], [0.0, 1.0]
    stream_close_to_id0, stream_close_to_id1 = [0.9, 0.1], [0.1, 0.9]
    assert assign_streams_open_set(
        [id0, id1], [stream_close_to_id0, stream_close_to_id1]
    ) == [0, 1]


def test_assign_streams_open_set_matches_existing_swapped() -> None:
    id0, id1 = [1.0, 0.0], [0.0, 1.0]
    stream_close_to_id1, stream_close_to_id0 = [0.1, 0.9], [0.9, 0.1]
    assert assign_streams_open_set(
        [id0, id1], [stream_close_to_id1, stream_close_to_id0]
    ) == [1, 0]


def test_assign_streams_open_set_unmatched_stream_becomes_new_speaker() -> None:
    id0 = [1.0, 0.0]
    stream_close_to_id0 = [0.95, 0.05]
    # orthogonal to id0, well below threshold — no known speaker to claim it
    unmatched_stream = [-1.0, 0.0]
    assert assign_streams_open_set([id0], [stream_close_to_id0, unmatched_stream]) == [0, 1]


def test_assign_streams_open_set_never_drops_a_stream_even_with_contention() -> None:
    # both streams are close to the single known speaker — only the best match claims it,
    # the other becomes a new speaker rather than being dropped (cf. ADR-0044, jamais de rejet)
    id0 = [1.0, 0.0]
    stream_a, stream_b = [0.99, 0.01], [0.9, 0.1]
    assignment = assign_streams_open_set([id0], [stream_a, stream_b])
    assert assignment == [0, 1]


def test_assign_streams_open_set_all_unmatched_become_sequential_new_speakers() -> None:
    id0, id1 = [1.0, 0.0], [0.0, 1.0]
    unmatched_a, unmatched_b = [-1.0, 0.0], [0.0, -1.0]
    assert assign_streams_open_set([id0, id1], [unmatched_a, unmatched_b]) == [2, 3]
