from __future__ import annotations

import asyncio
import queue
import threading
import time
import wave
from pathlib import Path
from typing import TYPE_CHECKING, Protocol

from loom_orchestrator.audio_mixer import DEFAULT_MAX_BUFFER_S, AudioMixer

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

    import numpy as np

# ⚠ Pas calibré empiriquement (ADR-0045) — quelques secondes de marge à 100ms/chunk, même
# esprit que `ROUTE_QUEUE_MAXSIZE` dans `bench/harness_pipeline_dual.py`.
DEFAULT_INPUT_QUEUE_SIZE = 20


class DropOldestBridge:
    """Pont thread-safe (`queue.Queue`, thread-safe par construction — pas de verrou manuel
    nécessaire) entre le thread audio (callback `sounddevice`, ne doit jamais bloquer) et la
    boucle asyncio (drainée via `asyncio.to_thread`). Politique drop-oldest si pleine, jamais
    de blocage côté producteur — même règle que `route_queue`/`send()` dans
    `bench/harness_pipeline_dual.py` (cf. Loom/CLAUDE.md, "jamais de blocage amont").
    """

    def __init__(self, maxsize: int = DEFAULT_INPUT_QUEUE_SIZE) -> None:
        self._queue: queue.Queue[bytes] = queue.Queue(maxsize=maxsize)
        self.dropped = 0

    def put_from_callback(self, chunk: bytes) -> None:
        try:
            self._queue.put_nowait(chunk)
        except queue.Full:
            self.dropped += 1
            try:
                self._queue.get_nowait()
            except queue.Empty:
                pass
            try:
                self._queue.put_nowait(chunk)
            except queue.Full:
                pass  # une autre poussée a gagné la course entre-temps — tant pis pour ce chunk

    def get_blocking(self, timeout: float = 0.5) -> bytes | None:
        """Bloquant (avec timeout) — à appeler uniquement via `asyncio.to_thread`, jamais
        depuis la boucle asyncio directement. `None` après `timeout` sans donnée : donne à
        `asyncio.to_thread` un point de retour périodique, sinon annuler la tâche appelante
        (Ctrl-C, cf. `capture_live`) ne peut jamais interrompre le thread bloqué sur un
        `queue.get()` sans fin — l'annulation asyncio ne force jamais l'arrêt d'un thread
        OS, elle ne fait que marquer la tâche annulée au prochain point de reprise.
        """
        try:
            return self._queue.get(timeout=timeout)
        except queue.Empty:
            return None

    def qsize(self) -> int:
        return self._queue.qsize()


async def capture_live(
    send: "Callable[[bytes], Awaitable[None]]",
    device: "int | str | None" = None,
    chunk_ms: int = 100,
    sample_rate_hz: int = 16000,
) -> None:
    """Capture micro en direct, même contrat que `bench/replay.py:replay_realtime` (`send`
    injecté) — `feed()` dans `main.py` change d'une seule ligne pour basculer entre rejeu
    fichier et capture live. 16kHz mono int16 : déjà le format attendu par `AudioProcessor`
    (`pcm_input=True`, cf. ADR-0039), pas de ré-échantillonnage en entrée.

    Ne se termine jamais (pas de "fin de fichier" en direct) — l'appelant doit annuler cette
    tâche pour arrêter la capture (cf. arrêt Ctrl-C dans `main.py`).
    """
    import sounddevice as sd

    bridge = DropOldestBridge()
    blocksize = int(sample_rate_hz * chunk_ms / 1000)

    def _callback(indata: "np.ndarray", frames: int, time_info: object, status: object) -> None:
        bridge.put_from_callback(indata.copy().tobytes())

    with sd.InputStream(
        samplerate=sample_rate_hz,
        channels=1,
        dtype="int16",
        blocksize=blocksize,
        device=device,
        callback=_callback,
    ):
        while True:
            chunk = await asyncio.to_thread(bridge.get_blocking)
            if chunk is not None:
                await send(chunk)


class OutputSink(Protocol):
    """Interface commune live/dry-run — `main.py` appelle `sink.push(ident, chunk)` sans
    jamais bifurquer sur le mode (ADR-0045)."""

    def push(self, ident: int, chunk: "np.ndarray") -> None: ...
    def close(self) -> None: ...


class LiveDeviceSink:
    """Sortie audio réelle — un `AudioMixer` (mixage au fil de l'eau, ADR-0045) dont le
    callback `sounddevice.OutputStream` tire des blocs à la cadence du device."""

    def __init__(
        self,
        sample_rate_hz: int,
        device: "int | str | None" = None,
        blocksize: int = 1024,
        max_buffer_s: float = DEFAULT_MAX_BUFFER_S,
    ) -> None:
        import sounddevice as sd

        self._mixer = AudioMixer(sample_rate_hz, max_buffer_s)
        self._stream = sd.OutputStream(
            samplerate=sample_rate_hz,
            channels=1,
            dtype="float32",
            device=device,
            blocksize=blocksize,
            callback=self._callback,
        )
        self._stream.start()

    def _callback(
        self, outdata: "np.ndarray", frames: int, time_info: object, status: object
    ) -> None:
        outdata[:, 0] = self._mixer.pull_mixed(frames)

    def push(self, ident: int, chunk: "np.ndarray") -> None:
        self._mixer.push(ident, chunk)

    def close(self) -> None:
        self._stream.stop()
        self._stream.close()


class DryRunWavSink:
    """Sortie de test sans matériel — même `AudioMixer` qu'en direct, mais un thread dédié
    tire des blocs à intervalle réel (`blocksize/sample_rate_hz`) au lieu du callback
    `sounddevice`, pour exercer fidèlement le mixage au fil de l'eau (cf. ADR-0045, étape 2-3
    de la séquence de vérification) sans dépendre d'un périphérique de sortie."""

    def __init__(
        self,
        path: Path,
        sample_rate_hz: int,
        blocksize: int = 1024,
        max_buffer_s: float = DEFAULT_MAX_BUFFER_S,
    ) -> None:
        self._mixer = AudioMixer(sample_rate_hz, max_buffer_s)
        self._sample_rate_hz = sample_rate_hz
        self._blocksize = blocksize
        self._path = path
        self._recorded_blocks: list["np.ndarray"] = []
        self._stop_event = threading.Event()
        self._thread = threading.Thread(target=self._pull_loop, daemon=True)
        self._thread.start()

    def _pull_loop(self) -> None:
        interval_s = self._blocksize / self._sample_rate_hz
        next_tick = time.monotonic()
        while not self._stop_event.is_set():
            self._recorded_blocks.append(self._mixer.pull_mixed(self._blocksize))
            next_tick += interval_s
            sleep_s = next_tick - time.monotonic()
            if sleep_s > 0:
                time.sleep(sleep_s)

    def push(self, ident: int, chunk: "np.ndarray") -> None:
        self._mixer.push(ident, chunk)

    def close(self) -> None:
        import numpy as np

        self._stop_event.set()
        self._thread.join(timeout=2.0)
        full_audio = (
            np.concatenate(self._recorded_blocks)
            if self._recorded_blocks
            else np.zeros(0, dtype=np.float32)
        )
        # Écrivain dupliqué depuis `bench/harness_pipeline.py:_write_wav` plutôt qu'importé —
        # `bench/` reste de l'outillage de mesure, `main.py` (production) ne doit pas en
        # dépendre (ADR-0045, même raisonnement que la relocalisation de `commit_state.py`).
        pcm16 = (np.clip(full_audio, -1.0, 1.0) * 32767.0).astype(np.int16)
        with wave.open(str(self._path), "wb") as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(2)
            wav_file.setframerate(self._sample_rate_hz)
            wav_file.writeframes(pcm16.tobytes())


def list_devices() -> str:
    import sounddevice as sd

    return str(sd.query_devices())
