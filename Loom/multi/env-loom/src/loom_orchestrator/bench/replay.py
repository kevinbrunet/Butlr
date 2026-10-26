from __future__ import annotations

import asyncio
import time
import wave
from collections.abc import Awaitable, Callable, Iterator
from dataclasses import dataclass
from pathlib import Path

MAX_REPLAY_RATE = 1.0
# ⚠ rate=1.0 (temps réel) est le seul mode d'usage normal ; rate<1.0 accepté pour du debug
# ponctuel (replay ralenti), rate>1.0 refusé explicitement — jamais de benchmark en lecture
# accélérée (règle transverse du backlog, T0.2).


@dataclass(frozen=True)
class AudioChunk:
    data: bytes
    start_sample: int
    n_samples: int
    sample_rate_hz: int

    @property
    def target_elapsed_s(self) -> float:
        return self.start_sample / self.sample_rate_hz


def iter_chunks(wav_path: Path, chunk_ms: int = 100) -> Iterator[AudioChunk]:
    """Découpe un wav 16kHz mono en chunks de taille fixe, dans l'ordre du flux.

    Fonction pure (pas d'I/O réseau, pas de sleep) : le pacing temps réel est appliqué
    séparément par `replay_realtime`, pour rester testable sans horloge murale.
    """
    with wave.open(str(wav_path), "rb") as wav_file:
        if wav_file.getnchannels() != 1:
            raise ValueError(f"{wav_path} : attendu mono, trouvé {wav_file.getnchannels()} canaux")

        sample_rate_hz = wav_file.getframerate()
        sample_width = wav_file.getsampwidth()
        frames_per_chunk = max(1, int(sample_rate_hz * chunk_ms / 1000))

        start_sample = 0
        while True:
            frames = wav_file.readframes(frames_per_chunk)
            if not frames:
                return
            n_samples = len(frames) // sample_width
            yield AudioChunk(
                data=frames,
                start_sample=start_sample,
                n_samples=n_samples,
                sample_rate_hz=sample_rate_hz,
            )
            start_sample += n_samples


async def replay_realtime(
    wav_path: Path,
    send: Callable[[bytes], Awaitable[None]],
    chunk_ms: int = 100,
    rate: float = 1.0,
) -> None:
    """Envoie les chunks d'un wav au rythme temps réel (throttle sur le sample rate).

    `send` est injecté (ex. `websocket.send`) pour garder cette fonction testable sans
    connexion réseau réelle.
    """
    if rate > MAX_REPLAY_RATE:
        raise ValueError(
            f"rate={rate} > {MAX_REPLAY_RATE} refusé — jamais de replay accéléré (cf. T0.2)."
        )

    replay_start = time.monotonic()

    for chunk in iter_chunks(wav_path, chunk_ms=chunk_ms):
        target_elapsed_s = chunk.target_elapsed_s / rate
        now_elapsed_s = time.monotonic() - replay_start
        sleep_s = target_elapsed_s - now_elapsed_s
        if sleep_s > 0:
            await asyncio.sleep(sleep_s)
        await send(chunk.data)
