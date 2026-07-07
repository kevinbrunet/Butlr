from __future__ import annotations

import argparse
import time

from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ~ Phrases FR représentatives — pas un corpus audio, juste des textes choisis pour couvrir
# une gamme de longueurs réalistes pour un tour de parole court en interprétariat (quelques
# mots à une phrase complète). Pas de source externe, écrites pour ce test.
SAMPLE_SENTENCES = [
    ("court", "Bonjour, comment allez-vous ?"),
    ("moyen", "Le chat noir traverse la rue tranquillement pendant que les enfants jouent."),
    (
        "long",
        "Nous avons besoin de mesurer précisément la latence de synthèse avant de décider "
        "si cette architecture peut tenir le budget d'interprétariat simultané.",
    ),
]

DEFAULT_REPEATS = 3


def run_probe(repeats: int = DEFAULT_REPEATS) -> list[tuple[str, int, int, float]]:
    """Isole la latence de `PocketTtsSynthesizer.synthesize()` — sans WLK ni Seamless, sans
    fichier de sortie. Sert à savoir si le plancher observé sur le premier run bout-en-bout
    (p50=2993ms, p95=4340ms sur des extraits de ~3 mots — cf. Révisions ADR-0036) est un
    phénomène systématique (coût fixe indépendant de la longueur du texte, ex. l'overhead
    du premier pas de décodage) ou un artefact du run bugué (segments trop courts/vides).

    Retourne `(label, n_mots, n_repeat, latence_ms)` par appel — pas de p50/p95 agrégé ici
    à dessein : `DEFAULT_REPEATS` est trop petit pour un percentile valable (cf.
    `aggregate._percentiles_ms`, qui l'exigerait aussi), l'intérêt est de voir la latence
    brute par longueur de phrase, pas une moyenne qui masquerait un éventuel coût fixe.
    """
    synth = PocketTtsSynthesizer()

    results: list[tuple[str, int, int, float]] = []
    for label, text in SAMPLE_SENTENCES:
        n_words = len(text.split())
        for i in range(repeats):
            t0 = time.monotonic()
            synth.synthesize(text)
            elapsed_ms = (time.monotonic() - t0) * 1000
            results.append((label, n_words, i, elapsed_ms))

    return results


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Sonde de latence Pocket TTS isolée (pas de WLK, pas de Seamless) — "
        "pour savoir si french_24l tient le budget de 400ms (STAGE_TTS) sur des phrases "
        "courtes, indépendamment du bug de scellement de tour de harness_pipeline.py."
    )
    parser.add_argument("--repeats", type=int, default=DEFAULT_REPEATS)
    args = parser.parse_args()

    results = run_probe(repeats=args.repeats)

    print(f"{'label':<8} {'mots':>5} {'run':>4} {'latence (ms)':>13}")
    for label, n_words, i, elapsed_ms in results:
        print(f"{label:<8} {n_words:>5} {i:>4} {elapsed_ms:>13.1f}")


if __name__ == "__main__":
    main()
