from __future__ import annotations

import threading
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ⚠ Pas calibré empiriquement (ADR-0045) — quelques secondes de marge avant qu'un identité
# muette/à la traîne ne perde de l'audio déjà synthétisé (politique drop-oldest, même esprit
# que `route_queue`/`partial_queues` dans `bench/harness_pipeline_dual.py`).
DEFAULT_MAX_BUFFER_S = 5.0


class IdentityAudioBuffer:
    """Tampon audio d'une seule identité — un `push(chunk)` par le thread qui exécute
    `_consume_stream` (TTS), des `pull(n_frames)` répétés par le callback de sortie
    audio (thread PortAudio). Les chunks TTS ne s'alignent jamais sur `n_frames` (taille de
    bloc du callback, fixée par le device) — stocker un tableau concaténé plutôt qu'une file
    de chunks distincts fait porter le report du reliquat par la simple troncature du
    tableau, sans bookkeeping séparé (ADR-0045).

    Verrouillé en interne (`threading.Lock`, pas `asyncio.Lock` — le callback audio ne
    tourne jamais dans la boucle asyncio) : `push`/`pull` sont individuellement thread-safe,
    même si le thread producteur (TTS) et le thread consommateur (callback audio) accèdent
    au même tampon en parallèle.
    """

    def __init__(self, sample_rate_hz: int, max_buffer_s: float = DEFAULT_MAX_BUFFER_S) -> None:
        import numpy as np

        self._max_samples = int(max_buffer_s * sample_rate_hz)
        self._samples = np.zeros(0, dtype=np.float32)
        self._lock = threading.Lock()
        self.dropped_samples = 0

    def push(self, chunk: "np.ndarray") -> None:
        import numpy as np

        with self._lock:
            self._samples = np.concatenate([self._samples, chunk.astype(np.float32)])
            overflow = len(self._samples) - self._max_samples
            if overflow > 0:
                self._samples = self._samples[overflow:]
                self.dropped_samples += overflow

    def pull(self, n_frames: int) -> "np.ndarray":
        """Retourne exactement `n_frames` échantillons — silence en fin de tampon si pas
        assez de matière (identité inactive en ce moment, pas une erreur)."""
        import numpy as np

        with self._lock:
            available = self._samples[:n_frames]
            self._samples = self._samples[n_frames:]
        if len(available) < n_frames:
            available = np.concatenate(
                [available, np.zeros(n_frames - len(available), dtype=np.float32)]
            )
        return available


class AudioMixer:
    """Registre de tampons par identité + mixage additif de sortie (ADR-0045 — mixage "au fil
    de l'eau", pas de recalage sur `identity_timeline`) : chaque identité pousse son audio dès
    qu'il est prêt, `pull_mixed` en lit un bloc en sommant toutes les identités actives à cet
    instant. Deux locuteurs qui se sont vraiment chevauchés dans la source démarrent
    traduction+TTS à des instants proches — leur audio se chevauche donc naturellement ici,
    sans jamais avoir eu besoin de calculer un décalage exact entre les deux.

    `threading.Lock` sur le registre lui-même (ajout/itération des identités, muté par
    `_ensure_identity` sur le thread asyncio, itéré par le callback audio) — distinct du
    verrou interne à chaque `IdentityAudioBuffer` (push/pull), volontairement pas tenu
    pendant la sommation elle-même pour ne pas bloquer l'ajout d'une nouvelle identité
    pendant qu'un bloc de sortie se mixe.
    """

    def __init__(self, sample_rate_hz: int, max_buffer_s: float = DEFAULT_MAX_BUFFER_S) -> None:
        self._sample_rate_hz = sample_rate_hz
        self._max_buffer_s = max_buffer_s
        self._buffers: dict[int, IdentityAudioBuffer] = {}
        self._lock = threading.Lock()

    def ensure_identity(self, ident: int) -> None:
        with self._lock:
            if ident not in self._buffers:
                self._buffers[ident] = IdentityAudioBuffer(self._sample_rate_hz, self._max_buffer_s)

    def push(self, ident: int, chunk: "np.ndarray") -> None:
        self.ensure_identity(ident)
        with self._lock:
            buffer = self._buffers[ident]
        buffer.push(chunk)

    def pull_mixed(self, n_frames: int) -> "np.ndarray":
        import numpy as np

        with self._lock:
            buffers = list(self._buffers.values())
        mixed = np.zeros(n_frames, dtype=np.float32)
        for buffer in buffers:
            mixed += buffer.pull(n_frames)
        return np.clip(mixed, -1.0, 1.0)
