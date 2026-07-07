from __future__ import annotations

import difflib

from loom_orchestrator.bench.instrumentation import STAGE_TTS


def diff_text(reference: str, actual: str) -> tuple[str, float]:
    """Diff mot à mot entre une traduction de référence et la sortie réelle du pipeline —
    pas un score BLEU/WER (hors scope de ce test), juste une inspection visuelle des écarts
    et un ratio de similarité grossier (`difflib.SequenceMatcher.ratio()`) pour trier les
    runs d'un coup d'œil.

    `-mot` = présent dans la référence mais absent de la sortie pipeline (omission/erreur de
    traduction). `+mot` = l'inverse (ajout/hallucination). Les mots identiques sont affichés
    sans préfixe.
    """
    ref_words = reference.split()
    actual_words = actual.split()
    matcher = difflib.SequenceMatcher(None, ref_words, actual_words)

    parts: list[str] = []
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            parts.extend(ref_words[i1:i2])
        elif tag == "delete":
            parts.extend(f"-{w}" for w in ref_words[i1:i2])
        elif tag == "insert":
            parts.extend(f"+{w}" for w in actual_words[j1:j2])
        elif tag == "replace":
            parts.extend(f"-{w}" for w in ref_words[i1:i2])
            parts.extend(f"+{w}" for w in actual_words[j1:j2])

    return " ".join(parts), matcher.ratio()


def first_output_latency_s(events: list[dict]) -> float | None:
    """Temps entre le début de la lecture de l'audio d'entrée (t=0, cf. `replay_realtime`)
    et le premier octet audio de sortie produit (premier chunk TTS, toutes lignes
    confondues) — une seule mesure bout-en-bout par run, pas la latence par segment (cf.
    `aggregate.py`). Retourne `None` si le run n'a produit aucun audio de sortie.
    """
    tts_out_times = [e["t_out"] for e in events if e["stage"] == STAGE_TTS]
    return min(tts_out_times) if tts_out_times else None
