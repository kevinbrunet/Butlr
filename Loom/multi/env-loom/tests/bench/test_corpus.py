from __future__ import annotations

import pytest

from loom_orchestrator.bench import corpus


def test_resolve_returns_path_under_corpus_dir(tmp_path) -> None:
    path = corpus.resolve("a", corpus_dir=tmp_path)
    assert path == tmp_path / "a_en_mono.wav"


def test_resolve_rejects_unknown_key(tmp_path) -> None:
    with pytest.raises(KeyError):
        corpus.resolve("z", corpus_dir=tmp_path)


def test_validate_raises_when_file_missing(tmp_path) -> None:
    with pytest.raises(corpus.CorpusValidationError):
        corpus.validate("c", corpus_dir=tmp_path)


def test_validate_rejects_wrong_sample_rate(tmp_path, write_silent_wav) -> None:
    write_silent_wav(tmp_path / "c_zh_mono.wav", 61.0, 8_000)
    with pytest.raises(corpus.CorpusValidationError, match="Hz"):
        corpus.validate("c", corpus_dir=tmp_path)


def test_validate_rejects_wrong_sample_width(tmp_path, write_silent_wav) -> None:
    write_silent_wav(tmp_path / "c_zh_mono.wav", 61.0, 16_000, sample_width_bytes=1)
    with pytest.raises(corpus.CorpusValidationError, match="bits"):
        corpus.validate("c", corpus_dir=tmp_path)


def test_validate_rejects_too_short_file(tmp_path, write_silent_wav) -> None:
    write_silent_wav(tmp_path / "c_zh_mono.wav", 5.0, 16_000)
    with pytest.raises(corpus.CorpusValidationError, match="attendu au moins"):
        corpus.validate("c", corpus_dir=tmp_path)


def test_validate_passes_for_conforming_file(tmp_path, write_silent_wav) -> None:
    write_silent_wav(tmp_path / "c_zh_mono.wav", 61.0, 16_000)
    corpus.validate("c", corpus_dir=tmp_path)  # ne lève pas
