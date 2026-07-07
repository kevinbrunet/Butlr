from __future__ import annotations

from loom_orchestrator.bench.audio_chunks import iter_duration_chunks, read_segment


def test_iter_duration_chunks_covers_whole_file(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=2.5, sample_rate_hz=16_000)  # 40000 samples

    chunks = list(iter_duration_chunks(wav_path, chunk_s=1.0))

    assert sum(c.n_samples for c in chunks) == 40_000
    assert len(chunks) == 3  # 1s, 1s, 0.5s


def test_iter_duration_chunks_produces_float32_normalized_audio(tmp_path, write_silent_wav) -> None:
    import numpy as np

    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=1.0, sample_rate_hz=16_000)

    chunks = list(iter_duration_chunks(wav_path, chunk_s=1.0))

    assert chunks[0].audio.dtype == np.float32
    assert np.max(np.abs(chunks[0].audio)) <= 1.0


def test_iter_duration_chunks_start_end_seconds(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=2.5, sample_rate_hz=16_000)

    chunks = list(iter_duration_chunks(wav_path, chunk_s=1.0))

    assert chunks[0].start_s == 0.0
    assert chunks[0].end_s == 1.0
    assert chunks[1].start_s == 1.0
    assert chunks[2].end_s == 2.5


def test_iter_duration_chunks_rejects_stereo(tmp_path) -> None:
    import wave

    import pytest

    wav_path = tmp_path / "stereo.wav"
    with wave.open(str(wav_path), "wb") as wav_file:
        wav_file.setnchannels(2)
        wav_file.setsampwidth(2)
        wav_file.setframerate(16_000)
        wav_file.writeframes(b"\x00\x00\x00\x00" * 100)

    with pytest.raises(ValueError, match="mono"):
        list(iter_duration_chunks(wav_path, chunk_s=1.0))


def test_read_segment_extracts_expected_sample_count(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=5.0, sample_rate_hz=16_000)

    audio = read_segment(wav_path, start_s=1.0, end_s=2.5)

    assert len(audio) == 1.5 * 16_000


def test_read_segment_produces_float32_normalized_audio(tmp_path, write_silent_wav) -> None:
    import numpy as np

    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=2.0, sample_rate_hz=16_000)

    audio = read_segment(wav_path, start_s=0.0, end_s=1.0)

    assert audio.dtype == np.float32
    assert np.max(np.abs(audio)) <= 1.0


def test_read_segment_from_start_of_file(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=1.0, sample_rate_hz=16_000)

    audio = read_segment(wav_path, start_s=0.0, end_s=1.0)

    assert len(audio) == 16_000


def test_read_segment_rejects_stereo(tmp_path) -> None:
    import wave

    import pytest

    wav_path = tmp_path / "stereo.wav"
    with wave.open(str(wav_path), "wb") as wav_file:
        wav_file.setnchannels(2)
        wav_file.setsampwidth(2)
        wav_file.setframerate(16_000)
        wav_file.writeframes(b"\x00\x00\x00\x00" * 100)

    with pytest.raises(ValueError, match="mono"):
        read_segment(wav_path, start_s=0.0, end_s=1.0)
