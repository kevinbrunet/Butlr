from __future__ import annotations

import time

import pytest

from loom_orchestrator.bench.replay import iter_chunks, replay_realtime


def test_iter_chunks_covers_whole_file_without_gaps_or_overlap(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=0.5, sample_rate_hz=16_000)  # 8000 samples

    chunks = list(iter_chunks(wav_path, chunk_ms=100))  # attendu : 5 chunks de 1600 samples

    assert sum(c.n_samples for c in chunks) == 8_000
    starts = [c.start_sample for c in chunks]
    assert starts == sorted(starts)
    assert starts[0] == 0


def test_iter_chunks_target_elapsed_matches_start_sample(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=0.3, sample_rate_hz=16_000)

    chunks = list(iter_chunks(wav_path, chunk_ms=100))

    for chunk in chunks:
        assert chunk.target_elapsed_s == pytest.approx(chunk.start_sample / 16_000)


def test_iter_chunks_rejects_stereo(tmp_path) -> None:
    import wave

    wav_path = tmp_path / "stereo.wav"
    with wave.open(str(wav_path), "wb") as wav_file:
        wav_file.setnchannels(2)
        wav_file.setsampwidth(2)
        wav_file.setframerate(16_000)
        wav_file.writeframes(b"\x00\x00\x00\x00" * 100)

    with pytest.raises(ValueError, match="mono"):
        list(iter_chunks(wav_path))


async def test_replay_realtime_rejects_accelerated_rate(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=0.1, sample_rate_hz=16_000)

    async def send(_: bytes) -> None:
        pass

    with pytest.raises(ValueError, match="jamais de replay accéléré"):
        await replay_realtime(wav_path, send, rate=1.5)


async def test_replay_realtime_paces_sends_to_wall_clock(tmp_path, write_silent_wav) -> None:
    wav_path = tmp_path / "test.wav"
    write_silent_wav(wav_path, duration_s=0.3, sample_rate_hz=16_000)

    sent = []

    async def send(chunk: bytes) -> None:
        sent.append(chunk)

    start = time.monotonic()
    await replay_realtime(wav_path, send, chunk_ms=100)
    elapsed_s = time.monotonic() - start

    assert len(sent) == 3
    assert elapsed_s == pytest.approx(0.3, abs=0.1)
