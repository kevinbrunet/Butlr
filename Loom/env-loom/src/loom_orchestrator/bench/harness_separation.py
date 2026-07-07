from __future__ import annotations

import argparse
import time
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.audio_chunks import read_segment
from loom_orchestrator.speaker_separation import SpeakerEmbedder, VoiceSeparator

DEFAULT_WINDOW_S = 6.0
DEFAULT_REPEATS = 3


def run_probe(
    corpus_key: str,
    corpus_dir: Path = corpus.CORPUS_DIR,
    window_s: float = DEFAULT_WINDOW_S,
    repeats: int = DEFAULT_REPEATS,
) -> list[tuple[int, float, float, float]]:
    """Isole la latence de `VoiceSeparator.separate()` + `SpeakerEmbedder.embed()` — sans
    WLK, sans Seamless, sans TTS. Sert à savoir quelle part du retard bout-en-bout constaté
    sur `harness_pipeline.py` (ADR-0042, run réel du 2026-07-15 : p95=13,6s) vient de cet
    étage précis, avant de décider quoi optimiser.

    Retourne `(repeat_idx, separate_s, embed_2streams_s, embed_mixture_s)` par appel — pas
    de p50/p95 agrégé ici à dessein (trop peu de repeats), l'intérêt est de voir la latence
    brute et si elle est stable d'un appel à l'autre.
    """
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    separator = VoiceSeparator()
    embedder = SpeakerEmbedder()

    window = read_segment(wav_path, 0.0, window_s)

    results: list[tuple[int, float, float, float]] = []
    for i in range(repeats):
        t0 = time.monotonic()
        streams = separator.separate(window)
        t_separate = time.monotonic() - t0

        t0 = time.monotonic()
        for stream in streams:
            embedder.embed(stream)
        t_embed_streams = time.monotonic() - t0

        t0 = time.monotonic()
        embedder.embed(window)
        t_embed_mixture = time.monotonic() - t0

        results.append((i, t_separate, t_embed_streams, t_embed_mixture))

    return results


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Sonde de latence séparation de voix + embedding isolée (ADR-0042) — "
        "pas de WLK, pas de Seamless, pas de TTS."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--window-s", type=float, default=DEFAULT_WINDOW_S)
    parser.add_argument("--repeats", type=int, default=DEFAULT_REPEATS)
    args = parser.parse_args()

    results = run_probe(args.corpus_key, args.corpus_dir, args.window_s, args.repeats)

    print(f"{'run':>4} {'separate (ms)':>14} {'embed 2 flux (ms)':>18} {'embed mélange (ms)':>19}")
    for i, t_separate, t_embed_streams, t_embed_mixture in results:
        print(
            f"{i:>4} {t_separate * 1000:>14.1f} {t_embed_streams * 1000:>18.1f} "
            f"{t_embed_mixture * 1000:>19.1f}"
        )


if __name__ == "__main__":
    main()
