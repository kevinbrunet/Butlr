from __future__ import annotations

import argparse
import re
import time
from dataclasses import dataclass
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import aggregate_by_stage, format_report, load_events
from loom_orchestrator.bench.evaluate import diff_text
from loom_orchestrator.bench.instrumentation import (
    STAGE_TRANSLATE_LLM,
    EventLogger,
    LatencyEvent,
)
from loom_orchestrator.bench.reference_transcripts import get_reference
from loom_orchestrator.translation_llm import LlmTranslator

# ⚠ Découpage naïf sur ponctuation forte (. ! ?) — approxime des segments de tour de parole
# pour mesurer une latence par appel représentative, pas un vrai découpage linguistique. À
# remplacer une fois la politique de commit retranchée (cf. ADR-0043 §Conséquences) ; ne
# gère pas les abréviations ("M.", "etc.") ni la ponctuation chinoise (corpus `c` a une
# seule phrase dans la référence actuelle, donc pas encore un problème observé).
_SENTENCE_SPLIT_RE = re.compile(r"(?<=[.!?])\s+")


def split_into_sentences(text: str) -> list[str]:
    return [s for s in _SENTENCE_SPLIT_RE.split(text.strip()) if s]


@dataclass
class LlmTranslateBenchmarkResult:
    log_path: Path
    transcript_path: Path
    ratio: float
    diff: str


def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    target_lang: str = "fr",
) -> LlmTranslateBenchmarkResult:
    """Validation isolée de la qualité/latence du petit Qwen de traduction (ADR-0043) —
    bypass complet de WLK/audio : traduit phrase par phrase le texte source *connu*
    (`reference_transcripts.py`, pas une transcription STT réelle), pour juger le modèle de
    traduction seul avant de câbler l'intégration complète.

    ⚠ Le texte source ici est exact (pas d'erreurs STT en amont, contrairement à un run
    pipeline complet, cf. `harness_evaluate.py`) — un score correct ici ne garantit pas un
    résultat correct une fois branché sur la sortie WLK réelle.
    """
    reference = get_reference(corpus_key)
    entry = next(c for c in corpus.CORPUS_MANIFEST if c.key == corpus_key)
    source_lang = entry.language

    run_id = f"{corpus_key}-llm-translate-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    out_dir.mkdir(parents=True, exist_ok=True)

    translator = LlmTranslator()
    sentences = split_into_sentences(reference.source_text)

    translated_parts: list[str] = []
    with (
        EventLogger(log_path) as logger,
        transcript_path.open("w", encoding="utf-8") as transcript,
    ):
        for idx, sentence in enumerate(sentences):
            t_start = time.monotonic()
            translated = translator.translate(sentence, source_lang, target_lang)
            t_end = time.monotonic()

            segment_id = f"{corpus_key}-llm-translate-sentence{idx}"
            duration_s = t_end - t_start
            logger.log(
                LatencyEvent.create(segment_id, STAGE_TRANSLATE_LLM, t_in=0.0, t_out=duration_s)
            )

            translated_parts.append(translated)
            transcript.write(f"[{idx}] {translated}\n")
            transcript.flush()

    actual = " ".join(translated_parts)
    diff, ratio = diff_text(reference.fr_reference, actual)

    return LlmTranslateBenchmarkResult(
        log_path=log_path, transcript_path=transcript_path, ratio=ratio, diff=diff
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Bench du petit Qwen de traduction (ADR-0043) : traduction phrase par "
        "phrase du texte source connu, sans passer par WLK/audio — validation qualité "
        "isolée avant de câbler l'intégration complète."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--target-lang", default="fr")
    args = parser.parse_args()

    result = run_benchmark(args.corpus_key, args.out_dir, target_lang=args.target_lang)

    events = load_events(result.log_path)
    reports = aggregate_by_stage(events)
    print(format_report(reports))
    print(f"\nSimilarité (difflib.ratio, indicatif) : {result.ratio:.1%}")
    print(f"Diff mot à mot (-référence / +traduction LLM) :\n{result.diff}\n")
    print(f"Log : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")


if __name__ == "__main__":
    main()
