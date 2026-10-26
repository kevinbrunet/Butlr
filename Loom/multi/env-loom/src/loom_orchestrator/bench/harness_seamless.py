from __future__ import annotations

import argparse
import time
from dataclasses import dataclass
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    format_report,
    load_events,
)
from loom_orchestrator.bench.audio_chunks import iter_duration_chunks
from loom_orchestrator.bench.instrumentation import STAGE_SEAMLESS, EventLogger, LatencyEvent
from loom_orchestrator.translation_seamless import SeamlessTranslator

DEFAULT_CHUNK_S = 10.0


@dataclass
class SeamlessBenchmarkResult:
    log_path: Path
    transcript_path: Path


def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    chunk_s: float = DEFAULT_CHUNK_S,
    target_lang: str = "fr",
) -> SeamlessBenchmarkResult:
    """Validation isolée de la qualité/latence de traduction Seamless (Phase 1, ADR-0040) —
    bypass complet de WLK/NLLB : découpe le corpus en segments de `chunk_s` secondes
    (approximation d'un tour de parole, en attendant que la diarisation WLK soit branchée,
    cf. T1.4) et traduit chaque segment directement via SeamlessM4T v2.

    ⚠ Ne mesure que l'étage de traduction en isolation — pas de STT, pas de diarisation
    réelle, pas de TTS. Sert uniquement à valider si Seamless corrige le problème
    d'hallucination NLLB (ADR-0040) avant de câbler l'intégration complète avec WLK.
    """
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    run_id = f"{corpus_key}-seamless-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    out_dir.mkdir(parents=True, exist_ok=True)

    translator = SeamlessTranslator()

    with EventLogger(log_path) as logger, transcript_path.open("w", encoding="utf-8") as transcript:
        for idx, chunk in enumerate(iter_duration_chunks(wav_path, chunk_s=chunk_s)):
            t_start = time.monotonic()
            text = translator.translate(chunk.audio, target_lang=target_lang)
            t_end = time.monotonic()

            segment_id = f"{corpus_key}-seamless-chunk{idx}"
            # t_in/t_out en secondes depuis le début du fichier, pas depuis le début du
            # process (contrairement au harnais WLK) : ici on mesure le temps de
            # traitement d'un segment déjà connu en entier, pas une latence de streaming.
            duration_s = t_end - t_start
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_SEAMLESS, t_in=0.0, t_out=duration_s
                )
            )

            transcript.write(f"[{chunk.start_s:.1f}s-{chunk.end_s:.1f}s] {text}\n")
            transcript.flush()

    return SeamlessBenchmarkResult(log_path=log_path, transcript_path=transcript_path)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Bench Seamless (Phase 1, ADR-0040) : traduction par segment, "
        "sans passer par WLK/NLLB — validation qualité isolée."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--chunk-s", type=float, default=DEFAULT_CHUNK_S)
    parser.add_argument("--target-lang", default="fr")
    args = parser.parse_args()

    result = run_benchmark(
        args.corpus_key,
        args.out_dir,
        args.corpus_dir,
        chunk_s=args.chunk_s,
        target_lang=args.target_lang,
    )

    events = load_events(result.log_path)
    reports = aggregate_by_stage(events)
    print(format_report(reports))
    print(f"\nLog : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")


if __name__ == "__main__":
    main()
