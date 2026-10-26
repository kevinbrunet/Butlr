from __future__ import annotations

import argparse
import asyncio
import time
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import load_events
from loom_orchestrator.bench.evaluate import diff_text, first_output_latency_s, format_evaluation
from loom_orchestrator.bench.harness_pipeline import run_benchmark
from loom_orchestrator.bench.reference_transcripts import get_reference


async def evaluate_corpus_key(corpus_key: str, out_dir: Path, corpus_dir: Path) -> str:
    """Fait tourner le pipeline complet (`harness_pipeline.run_benchmark`) sur `corpus_key`,
    puis compare la sortie FR à une traduction de référence rédigée à la main (cf.
    `reference_transcripts.py` — pas une traduction certifiée, une base de comparaison pour
    repérer les régressions). Retourne le rapport formaté (cf. `evaluate.format_evaluation`)
    — l'appelant décide de l'afficher et/ou de l'écrire dans un fichier.
    """
    result = await run_benchmark(corpus_key, out_dir, corpus_dir)
    reference = get_reference(corpus_key)

    actual_text = "\n".join(
        result.final_fr_by_line[idx] for idx in sorted(result.final_fr_by_line)
    )
    diff, ratio = diff_text(reference.fr_reference, actual_text)

    events = load_events(result.log_path)
    latency_s = first_output_latency_s(events)

    return format_evaluation(
        corpus_key,
        reference.provenance,
        ratio,
        diff,
        latency_s,
        str(result.audio_dir),
        str(result.transcript_path),
        str(result.log_path),
    )


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

    args.out_dir.mkdir(parents=True, exist_ok=True)
    report_path = args.out_dir / f"evaluate-report-{int(time.time())}.txt"

    async def run_all() -> None:
        # Réécrit le fichier de rapport après chaque clé (pas seulement à la fin) : un run
        # multi-clés interrompu ou dont le scrollback du terminal déborde garde quand même
        # les résultats déjà obtenus (cf. run du 2026-07-15 où seule la dernière clé était
        # encore visible dans le terminal).
        reports: list[str] = []
        for key in args.corpus_keys:
            report = await evaluate_corpus_key(key, args.out_dir, args.corpus_dir)
            print(report)
            reports.append(report)
            report_path.write_text("\n".join(reports), encoding="utf-8")
        print(f"Rapport complet (toutes les clés) : {report_path}")

    asyncio.run(run_all())


if __name__ == "__main__":
    main()
