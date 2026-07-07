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


def format_evaluation(
    corpus_key: str,
    provenance: str,
    ratio: float,
    diff: str,
    latency_s: float | None,
    audio_dir: str,
    transcript_path: str,
    log_path: str,
) -> str:
    """Formatte un rapport pour une clé de corpus — pur (pas d'I/O), pour que l'appelant
    (`harness_evaluate.py`) puisse à la fois l'afficher et l'écrire dans un fichier de
    rapport cumulatif (cf. le scrollback perdu constaté par Kevin sur un run multi-clés)."""
    latency_str = f"{latency_s:.2f}s" if latency_s is not None else "aucun audio produit"
    return (
        f"=== corpus {corpus_key} ===\n"
        f"Référence : {provenance}\n"
        f"Similarité (difflib.ratio, indicatif — pas un score BLEU/WER) : {ratio:.1%}\n"
        f"Latence premier son de sortie (lecture wav entrée -> écriture wav sortie) : "
        f"{latency_str}\n"
        f"Diff mot à mot (-référence / +pipeline) :\n{diff}\n\n"
        f"Audio à écouter : {audio_dir}\n"
        f"Transcript : {transcript_path}\n"
        f"Log latences : {log_path}\n"
    )
