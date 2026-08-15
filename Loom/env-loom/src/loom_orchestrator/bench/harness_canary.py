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
from loom_orchestrator.bench.instrumentation import STAGE_CANARY, EventLogger, LatencyEvent
from loom_orchestrator.translation_canary import AlignAttCanaryTranslator

DEFAULT_CHUNK_S = 10.0

# ⚠ ADR-0047 : Canary-1B-v2 ne couvre pas le mandarin — seuls les corpus EN ont un sens ici
# (`a`, `b`, `d`, `e`, `f`, `g`, cf. `corpus.CORPUS_MANIFEST`). `c` (zh) est exclu par
# construction, pas juste par convention d'usage.
EXCLUDED_LANGUAGE = "zh"


@dataclass
class CanaryBenchmarkResult:
    log_path: Path
    transcript_path: Path


def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    chunk_s: float = DEFAULT_CHUNK_S,
    target_lang: str = "fr",
) -> CanaryBenchmarkResult:
    """Validation isolée de la qualité/latence de traduction Canary-1B-v2 (ADR-0047) — bypass
    complet de WLK : découpe le corpus en segments de `chunk_s` secondes (même approximation
    qu'un tour de parole que `harness_seamless.py`, pas la vraie diarisation WLK) et traduit
    chaque segment directement via `AlignAttCanaryTranslator.translate`.

    ⚠ Ne mesure que l'étage de traduction en isolation, en mode "un segment = un appel complet"
    — pas encore la politique de décodage streaming intra-segment (`AEDStreamingDecodingConfig`
    est configurée dans `AlignAttCanaryTranslator.__init__`, mais `translate()` appelle
    `.transcribe()` sur un segment déjà borné par `chunk_s`, pas sur un flux continu). Sert à
    valider en premier si le bug connu `NVIDIA-NeMo/NeMo#15231` (blocage après ~20-40s sur audio
    long en streaming) se manifeste ici, et si la qualité EN→FR est au moins comparable à Seamless
    (`harness_seamless.py`) / au petit LLM (`harness_llm_translate.py`) — avant tout câblage dans
    `harness_pipeline.py`.
    """
    corpus_key_entry = next(c for c in corpus.CORPUS_MANIFEST if c.key == corpus_key)
    if corpus_key_entry.language == EXCLUDED_LANGUAGE:
        raise ValueError(
            f"corpus {corpus_key!r} en {corpus_key_entry.language!r} — Canary-1B-v2 ne "
            "supporte pas le mandarin (cf. ADR-0047), choisis un corpus EN."
        )

    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    run_id = f"{corpus_key}-canary-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    out_dir.mkdir(parents=True, exist_ok=True)

    translator = AlignAttCanaryTranslator()

    with EventLogger(log_path) as logger, transcript_path.open("w", encoding="utf-8") as transcript:
        for idx, chunk in enumerate(iter_duration_chunks(wav_path, chunk_s=chunk_s)):
            t_start = time.monotonic()
            text = translator.translate(chunk.audio, target_lang=target_lang)
            t_end = time.monotonic()

            segment_id = f"{corpus_key}-canary-chunk{idx}"
            duration_s = t_end - t_start
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_CANARY, t_in=0.0, t_out=duration_s
                )
            )

            transcript.write(f"[{chunk.start_s:.1f}s-{chunk.end_s:.1f}s] {text}\n")
            transcript.flush()

    return CanaryBenchmarkResult(log_path=log_path, transcript_path=transcript_path)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Bench Canary-1B-v2 + AlignAtt natif NeMo (ADR-0047) : traduction EN→FR "
        "par segment, sans passer par WLK — validation qualité/latence isolée. Chinois exclu "
        "(Canary ne le supporte pas)."
    )
    parser.add_argument(
        "corpus_key",
        choices=[c.key for c in corpus.CORPUS_MANIFEST if c.language != EXCLUDED_LANGUAGE],
    )
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
