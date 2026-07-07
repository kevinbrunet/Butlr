from __future__ import annotations

import argparse
import asyncio
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import load_events
from loom_orchestrator.bench.evaluate import diff_text, first_output_latency_s
from loom_orchestrator.bench.harness_pipeline import run_benchmark
from loom_orchestrator.bench.reference_transcripts import get_reference


async def evaluate_corpus_key(corpus_key: str, out_dir: Path, corpus_dir: Path) -> None:
    """Fait tourner le pipeline complet (`harness_pipeline.run_benchmark`) sur `corpus_key`,
    puis compare la sortie FR à une traduction de référence rédigée à la main (cf.
    `reference_transcripts.py` — pas une traduction certifiée, une base de comparaison pour
    repérer les régressions). Affiche un diff mot à mot, la latence jusqu'au premier son de
    sortie, et les chemins vers l'audio et le transcript à écouter/lire.
    """
    result = await run_benchmark(corpus_key, out_dir, corpus_dir)
    reference = get_reference(corpus_key)

    actual_text = "\n".join(
        result.final_fr_by_line[idx] for idx in sorted(result.final_fr_by_line)
    )
    diff, ratio = diff_text(reference.fr_reference, actual_text)

    events = load_events(result.log_path)
    latency_s = first_output_latency_s(events)
    latency_str = f"{latency_s:.2f}s" if latency_s is not None else "aucun audio produit"

    print(f"=== corpus {corpus_key} ===")
    print(f"Référence : {reference.provenance}")
    print(f"Similarité (difflib.ratio, indicatif — pas un score BLEU/WER) : {ratio:.1%}")
    print(f"Latence premier son de sortie (lecture wav entrée -> écriture wav sortie) : "
          f"{latency_str}")
    print(f"Diff mot à mot (-référence / +pipeline) :\n{diff}\n")
    print(f"Audio à écouter : {result.audio_dir}")
    print(f"Transcript : {result.transcript_path}")
    print(f"Log latences : {result.log_path}\n")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Évalue le pipeline bout-en-bout sur tout ou partie du corpus : diff FR "
        "mot à mot vs référence rédigée à la main, latence premier son de sortie, chemins "
        "vers l'audio et le transcript."
    )
    parser.add_argument(
        "corpus_keys",
        nargs="*",
        default=[c.key for c in corpus.CORPUS_MANIFEST],
        help="Clés du corpus à évaluer (défaut : tout le corpus).",
    )
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    args = parser.parse_args()

    known_keys = {c.key for c in corpus.CORPUS_MANIFEST}
    unknown = set(args.corpus_keys) - known_keys
    if unknown:
        parser.error(f"clés de corpus inconnues : {sorted(unknown)} — attendu {sorted(known_keys)}")

    async def run_all() -> None:
        for key in args.corpus_keys:
            await evaluate_corpus_key(key, args.out_dir, args.corpus_dir)

    asyncio.run(run_all())


if __name__ == "__main__":
    main()
