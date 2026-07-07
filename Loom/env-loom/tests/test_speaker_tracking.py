from __future__ import annotations

from loom_orchestrator.speaker_tracking import (
    cosine_similarity,
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
