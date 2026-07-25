from __future__ import annotations

import argparse
import time
from pathlib import Path

from loom_orchestrator.commit_state import _consume_continuation
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ✓ Increments FR réels, copiés tels quels du transcript produit par `main.py` sur
# `corpus a` (run live-1784662066, 2026-07-21) — pas un texte de test synthétique. Chaque
# élément correspond à un appel `synthesize_continuation` réel dans ce run, sur le même
# `voice_state` continu (une seule ligne WLK, id0, tout le chapitre). Isole la question :
# est-ce que la répétition audio observée (STT sur le WAV mixé montrant "dans le trou de
# lapin" en boucle ~139s) vient de Pocket TTS lui-même sur un `voice_state` étiré sur
# beaucoup d'appels consécutifs, indépendamment de WLK/traduction/mixeur/threading ?
REAL_INCREMENTS = [
    "LibriVox.",
    "org.",
    "Les aventures d'Alice dans le pays des merveilles de Lewis Carroll.",
    "Chapitre 1.",
    "Dans le trou de lapin.",
    "Alice commençait à s'ennuyer beaucoup de rester assise auprès de sa sœur sur la rive "
    "et d'avoir rien à faire.",
    "Une ou deux fois,",
    "Elle jeta un coup d'œil dans le livre que sa sœur lisait.",
    "mais il ne contenait ni photos ni conversations.",
    "Et à quoi sert un livre ?",
    "pensait Alice sans images ni conversation ?",
    "Ainsi, elle se demandait dans son esprit,",
    "aussi bien que possible",
    "pour la chaleur de la journée qui lui faisait sentir très somnolente et stupide,",
    "si le plaisir de faire une chaîne de marguerites vaut la peine du trouble de se lever "
    "et de ramasser les marguerites.",
    "Lorsqu'il arrive soudainement,",
    "Un lapin blanc aux yeux roses courut près d'elle.",
    "Il n'y avait rien de très remarquable à cela.",
    "Nordid Alice penser qu'il était si très étrange.",
    "Elle aurait dû lui faire dire à lui-même,",
    '"Oh mon Dieu, oh mon Dieu,"',
    "Je serai en retard.",
    "Lorsqu'elle y réfléchit par la suite,",
    "Elle se rendit compte qu'elle aurait dû indiquer cela.",
    "mais à l'époque, c'était tout à fait naturel.",
    "Mais quand le lapin en sortit un chronomètre de sa poche de veste, le regarda et puis "
    "s'en alla rapidement,",
    "Alice se leva aussitôt, car l'image lui traversa l'esprit qu'elle n'avait jamais vu de "
    "lapin possédant un poche de veste ou une montre qu'on pourrait en sortir, et elle "
    "brûlait d'curiosité.",
    "Elle est partie sur le champ après lui. Malheureusement,",
    "C'était juste à temps pour le voir disparaître dans un grand trou de lapin sous le "
    "haubert.",
    "Dans un autre instant,",
]


def run_probe(
    synth: PocketTtsSynthesizer, increments: list[str], reset_every: int | None = None
) -> tuple[list[float], list[int], list]:
    """Rejoue `increments` sur un `voice_state` (`new_line_state()`, puis
    `synthesize_continuation()` en boucle) — même schéma d'appel que `emit_increment` dans
    `main.py`/`bench/harness_pipeline.py`, sans WLK/traduction/mixeur.

    `reset_every` : si posé, réinitialise `state` (nouveau `new_line_state()`) toutes les
    `reset_every` increments au lieu de chaîner l'intégralité de la ligne sur un seul état —
    hypothèse à tester : la répétition en boucle observée sur un contexte de continuation
    long (cf. maintainers Pocket TTS, issue #151 — les chunks longs sont généralement générés
    indépendamment, pas chaînés indéfiniment via `copy_state=False`) disparaît si on borne la
    longueur de chaînage. `None` (défaut) = comportement actuel de production, sans reset.

    Retourne `(ttfc_ms_par_appel, n_chunks_par_appel, tous_les_chunks_concatenés)`.
    """
    state = synth.new_line_state()

    ttfc_ms_list: list[float] = []
    n_chunks_list: list[int] = []
    all_chunks = []
    for i, text in enumerate(increments):
        if reset_every is not None and i > 0 and i % reset_every == 0:
            state = synth.new_line_state()
            print(f"DEBUG increment {i + 1}: reset du voice_state (reset_every={reset_every})")
        t0 = time.monotonic()
        chunks, ttfc_s = _consume_continuation(synth, state, text)
        ttfc_ms_list.append(ttfc_s * 1000)
        n_chunks_list.append(len(chunks))
        all_chunks.extend(chunks)
        wall_ms = (time.monotonic() - t0) * 1000
        print(
            f"DEBUG increment {i + 1}: n_chunks={len(chunks)} "
            f"ttfc={ttfc_s * 1000:.0f}ms wall={wall_ms:.0f}ms texte={text!r}"
        )

    return ttfc_ms_list, n_chunks_list, all_chunks


def _write_wav(path: Path, chunks: list, sample_rate_hz: int) -> None:
    import wave

    import numpy as np

    audio = np.concatenate(chunks) if chunks else np.zeros(0, dtype=np.float32)
    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Sonde isolée : rejoue une longue suite d'increments réels sur un seul "
        "voice_state continu (Pocket TTS, synthesize_continuation) — sans WLK, sans "
        "traduction, sans mixeur — pour savoir si la répétition audio observée en usage "
        "réel vient de Pocket TTS sur un contexte long."
    )
    parser.add_argument("--out-wav", type=Path, default=Path("/tmp/harness-tts-continuation.wav"))
    parser.add_argument(
        "--n-increments",
        type=int,
        default=len(REAL_INCREMENTS),
        help="Nombre d'increments à rejouer (par défaut tous, %(default)s).",
    )
    parser.add_argument(
        "--reset-every",
        type=int,
        default=None,
        help="Réinitialise le voice_state toutes les N increments au lieu de chaîner toute "
        "la ligne sur un seul état continu (défaut : pas de reset, comportement actuel de "
        "production).",
    )
    args = parser.parse_args()

    increments = REAL_INCREMENTS[: args.n_increments]
    synth = PocketTtsSynthesizer()

    _, _, all_chunks = run_probe(synth, increments, reset_every=args.reset_every)
    _write_wav(args.out_wav, all_chunks, synth.sample_rate_hz)
    print(f"\nWAV écrit : {args.out_wav}")


if __name__ == "__main__":
    main()
