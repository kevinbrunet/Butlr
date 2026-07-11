from __future__ import annotations

import argparse
import time
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.audio_chunks import read_segment
from loom_orchestrator.bench.harness_pipeline import _write_wav
from loom_orchestrator.speaker_separation import SAMPLE_RATE_HZ, SpeakerEmbedder, VoiceSeparator
from loom_orchestrator.speaker_tracking import cosine_similarity, streams_are_distinct

DEFAULT_WINDOW_S = 6.0
DEFAULT_REPEATS = 3


def dump_separated_streams(
    corpus_key: str, out_dir: Path, corpus_dir: Path = corpus.CORPUS_DIR, window_s: float = 6.0
) -> None:
    """Sépare les `window_s` premières secondes de `corpus_key` et écrit les 2 flux + le
    mélange brut en wav dans `out_dir`, pour juger la qualité de séparation à l'oreille —
    contrairement à `run_probe`, qui ne mesure que la latence sans jamais rien écrire.
    Rapporte aussi `streams_are_distinct` et la similarité cosinus entre les 2 flux (proche de
    1 = quasi identiques, rien de réel à séparer ; proche de 0/négatif = bien distincts).
    """
    import numpy as np

    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)
    window = read_segment(wav_path, 0.0, window_s)

    separator = VoiceSeparator()
    embedder = SpeakerEmbedder()

    streams = separator.separate(window)
    embeddings = [embedder.embed(s) for s in streams]

    out_dir.mkdir(parents=True, exist_ok=True)
    _write_wav(out_dir / f"{corpus_key}-mixture.wav", window, SAMPLE_RATE_HZ)
    for i, stream in enumerate(streams):
        _write_wav(out_dir / f"{corpus_key}-stream{i}.wav", np.asarray(stream), SAMPLE_RATE_HZ)

    similarity = cosine_similarity(embeddings[0], embeddings[1])
    distinct = streams_are_distinct(embeddings)
    print(f"streams_are_distinct = {distinct} (similarité flux0/flux1 = {similarity:.2f})")
    print(f"Fichiers écrits dans {out_dir}/ : {corpus_key}-mixture.wav, "
          f"{corpus_key}-stream0.wav, {corpus_key}-stream1.wav")


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
    parser.add_argument(
        "--dump-audio",
        type=Path,
        default=None,
        metavar="OUT_DIR",
        help="Écrit les 2 flux séparés + le mélange en wav dans OUT_DIR pour écoute, au lieu "
        "de mesurer la latence.",
    )
    args = parser.parse_args()

    if args.dump_audio is not None:
        dump_separated_streams(args.corpus_key, args.dump_audio, args.corpus_dir, args.window_s)
        return

    results = run_probe(args.corpus_key, args.corpus_dir, args.window_s, args.repeats)

    print(f"{'run':>4} {'separate (ms)':>14} {'embed 2 flux (ms)':>18} {'embed mélange (ms)':>19}")
    for i, t_separate, t_embed_streams, t_embed_mixture in results:
        print(
            f"{i:>4} {t_separate * 1000:>14.1f} {t_embed_streams * 1000:>18.1f} "
            f"{t_embed_mixture * 1000:>19.1f}"
        )


if __name__ == "__main__":
    main()
