from __future__ import annotations

import argparse
import re
from pathlib import Path

from loom_orchestrator.bench.evaluate import diff_text
from loom_orchestrator.bench.reference_transcripts import _MOBY_DICK_FR, _TOM_SAWYER_FR

# Format écrit par `emit_increment` (harness_pipeline_dual.py) : "[id{ident}/{speaker}] FR
# (increment) : {texte}".
_LINE_RE = re.compile(r"^\[id(\d+)/[^\]]*\] FR \(increment\) : (.*)$")

# `corpus_b` (et ses alias e/f, cf. reference_transcripts.py) mélange ces deux livres — pas
# d'autre corpus multi-locuteur avec référence connue à ce jour.
_REFERENCES = {
    "Tom Sawyer": _TOM_SAWYER_FR,
    "Moby Dick": _MOBY_DICK_FR,
}


def group_by_identity(transcript_path: Path) -> dict[int, str]:
    """Regroupe les incréments FR d'un transcript `harness_pipeline_dual` par identité —
    concatène dans l'ordre d'apparition dans le fichier. Pure hors la lecture du fichier
    (aucune dépendance GPU/torch), contrairement au reste de `harness_evaluate_dual`."""
    by_ident: dict[int, list[str]] = {}
    for line in transcript_path.read_text(encoding="utf-8").splitlines():
        match = _LINE_RE.match(line)
        if not match:
            continue
        ident = int(match.group(1))
        by_ident.setdefault(ident, []).append(match.group(2))
    return {ident: " ".join(parts) for ident, parts in by_ident.items()}


def best_match(actual: str) -> tuple[str, str, float]:
    """Diffe `actual` contre chaque référence connue (`_REFERENCES`), retourne
    `(nom_livre, diff, ratio)` de la meilleure — l'identité qui produit ce texte n'est pas
    connue a priori, la numérotation dépend de l'ordre d'assignation au runtime (référentiel
    de locuteurs ouvert, ADR-0044), pas d'un mapping fixe id→livre.
    """
    best_name, best_diff, best_ratio = "?", "", -1.0
    for name, reference in _REFERENCES.items():
        diff, ratio = diff_text(reference, actual)
        if ratio > best_ratio:
            best_name, best_diff, best_ratio = name, diff, ratio
    return best_name, best_diff, best_ratio


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Diff mot à mot du transcript d'un run harness_pipeline_dual (ADR-0044) "
        "contre les deux références connues de corpus b (Tom Sawyer, Moby Dick) — une "
        "identité à la fois plutôt qu'un flux unique (contrairement à harness_evaluate.py, "
        "qui suppose un seul WLK/une seule sortie et ne s'applique pas au référentiel de "
        "locuteurs ouvert)."
    )
    parser.add_argument("transcript_path", type=Path)
    args = parser.parse_args()

    by_ident = group_by_identity(args.transcript_path)
    if not by_ident:
        print(f"Aucune ligne '[idN/...] FR (increment) :' trouvée dans {args.transcript_path}")
        return

    for ident in sorted(by_ident):
        actual = by_ident[ident]
        n_words = len(actual.split())
        name, diff, ratio = best_match(actual)
        print(f"=== id{ident} ({n_words} mots produits, meilleur match : {name}, ratio={ratio:.2f}) ===")
        print(diff)
        print()


if __name__ == "__main__":
    main()
