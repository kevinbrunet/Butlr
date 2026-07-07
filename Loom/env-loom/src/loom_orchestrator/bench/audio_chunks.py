from __future__ import annotations

import wave
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np


@dataclass(frozen=True)
class DurationChunk:
    audio: "np.ndarray"
    start_sample: int
    n_samples: int
    sample_rate_hz: int

    @property
    def start_s(self) -> float:
        return self.start_sample / self.sample_rate_hz

    @property
    def end_s(self) -> float:
        return (self.start_sample + self.n_samples) / self.sample_rate_hz


def read_segment(wav_path: Path, start_s: float, end_s: float) -> "np.ndarray":
    """Extrait `[start_s, end_s)` d'un wav 16kHz mono PCM16, en float32 normalisé [-1, 1].

    Contrairement à `iter_duration_chunks` (segments de taille fixe, pour un balayage
    séquentiel du fichier), ici la fenêtre est arbitraire : sert à extraire l'audio source
    d'un tour de parole une fois ses bornes connues (`line["start"]`/`line["end"]` de WLK),
    pour l'envoyer à Seamless (cf. `bench/harness_pipeline.py`).
    """
    import numpy as np

    with wave.open(str(wav_path), "rb") as wav_file:
        if wav_file.getnchannels() != 1:
            raise ValueError(f"{wav_path} : attendu mono, trouvé {wav_file.getnchannels()} canaux")
        sample_rate_hz = wav_file.getframerate()
        start_sample = max(0, int(start_s * sample_rate_hz))
        end_sample = max(start_sample, int(end_s * sample_rate_hz))
        wav_file.setpos(start_sample)
        frames = wav_file.readframes(end_sample - start_sample)

    pcm16 = np.frombuffer(frames, dtype=np.int16)
    return pcm16.astype(np.float32) / 32768.0


def iter_duration_chunks(wav_path: Path, chunk_s: float) -> "list[DurationChunk]":
    """Découpe un wav 16kHz mono PCM16 en segments de `chunk_s` secondes, en float32
    normalisé [-1, 1] (format attendu par le processeur SeamlessM4T v2).

    Contrairement à `replay.iter_chunks` (chunks courts pour le streaming temps réel),
    ici les segments sont des tours de parole complets (Phase 1 de ADR-0040 — Seamless
    n'est pas un modèle streaming mot-à-mot). Le dernier segment peut être plus court que
    `chunk_s` s'il ne reste pas assez d'échantillons.
    """
    import numpy as np

    with wave.open(str(wav_path), "rb") as wav_file:
        if wav_file.getnchannels() != 1:
            raise ValueError(f"{wav_path} : attendu mono, trouvé {wav_file.getnchannels()} canaux")
        sample_rate_hz = wav_file.getframerate()
        frames_per_chunk = max(1, int(sample_rate_hz * chunk_s))

        chunks = []
        start_sample = 0
        while True:
            frames = wav_file.readframes(frames_per_chunk)
            if not frames:
                return chunks
            pcm16 = np.frombuffer(frames, dtype=np.int16)
            audio = pcm16.astype(np.float32) / 32768.0
            chunks.append(
                DurationChunk(
                    audio=audio,
                    start_sample=start_sample,
                    n_samples=len(pcm16),
                    sample_rate_hz=sample_rate_hz,
                )
            )
            start_sample += len(pcm16)
