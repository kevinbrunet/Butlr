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


def run_probe(repeats: int = DEFAULT_REPEATS) -> list[tuple[str, int, int, float, float, int]]:
    """Isole la latence de `PocketTtsSynthesizer.synthesize_stream()` — sans WLK ni Seamless,
    sans fichier de sortie.

    ⚠ Une première version utilisait `synthesize()` (bloquant, `generate_audio`) : ~300-450ms
    par mot, pas un coût fixe (7s sur 22 mots) — mauvaise API pour le budget de ADR-0036, qui
    porte sur le time-to-first-chunk (streaming), pas le temps de synthèse total d'un énoncé
    complet. `synthesize_stream()` mesure la métrique qui compte réellement.

    Retourne `(label, n_mots, n_repeat, ttfc_ms, total_ms, n_chunks)` par appel — pas de
    p50/p95 agrégé ici à dessein : `DEFAULT_REPEATS` est trop petit pour un percentile
    valable (cf. `aggregate._percentiles_ms`, qui l'exigerait aussi), l'intérêt est de voir
    la latence brute par longueur de phrase.
    """
    synth = PocketTtsSynthesizer()

    results: list[tuple[str, int, int, float, float, int]] = []
    for label, text in SAMPLE_SENTENCES:
        n_words = len(text.split())
        for i in range(repeats):
            t0 = time.monotonic()
            ttfc_ms: float | None = None
            n_chunks = 0
            for _chunk in synth.synthesize_stream(text):
                if ttfc_ms is None:
                    ttfc_ms = (time.monotonic() - t0) * 1000
                n_chunks += 1
            total_ms = (time.monotonic() - t0) * 1000
            results.append((label, n_words, i, ttfc_ms or total_ms, total_ms, n_chunks))

    return results


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Sonde de latence Pocket TTS isolée (pas de WLK, pas de Seamless) — "
        "mesure le time-to-first-chunk (budget ADR-0036, 400ms) via generate_audio_stream, "
        "pas le temps de synthèse total d'un énoncé complet."
    )
    parser.add_argument("--repeats", type=int, default=DEFAULT_REPEATS)
    args = parser.parse_args()

    results = run_probe(repeats=args.repeats)

    print(f"{'label':<8} {'mots':>5} {'run':>4} {'ttfc (ms)':>10} {'total (ms)':>11} {'chunks':>7}")
    for label, n_words, i, ttfc_ms, total_ms, n_chunks in results:
        print(
            f"{label:<8} {n_words:>5} {i:>4} {ttfc_ms:>10.1f} {total_ms:>11.1f} {n_chunks:>7}"
        )


if __name__ == "__main__":
    main()
