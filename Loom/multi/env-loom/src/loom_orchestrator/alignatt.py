from __future__ import annotations


def safe_token_count(attended_frames: list[int], encoder_seq_len: int, frontier_frames: int) -> int:
    """AlignAtt (Papi et al., Interspeech 2023, arxiv 2305.11408) : un token généré est "sûr"
    à émettre si l'argmax de son attention croisée pointe vers une frame source à plus de
    `frontier_frames` de la fin de l'audio actuellement disponible (encoder_seq_len). Dès
    qu'un token n'est pas sûr, tous les tokens suivants de la séquence sont considérés
    incertains aussi — pas de retour en arrière une fois un token jugé incertain, cf.
    l'hypothèse de monotonie de l'attention en parole du papier original.

    `attended_frames[i]` = index de frame source (0-indexé) le plus attendu pour le i-ème
    token généré. Fonction pure — la lecture des tensors d'attention réels (transformers,
    `generate(output_attentions=True)`) est isolée dans `translation_seamless.py`, pas ici.
    """
    frontier = encoder_seq_len - frontier_frames
    count = 0
    for attended_frame in attended_frames:
        if attended_frame < frontier:
            count += 1
        else:
            break
    return count


def compute_increment(committed: str, new_safe_text: str) -> tuple[str, bool]:
    """Diff entre le texte déjà commité (envoyé au TTS) pour une ligne et le nouveau texte
    "sûr" recalculé sur un audio plus long.

    Retourne `(increment, is_consistent)` :
    - `increment` : la partie de `new_safe_text` au-delà de `committed`, à envoyer au TTS.
      Vide si rien de nouveau (ou si incohérent, cf. ci-dessous).
    - `is_consistent` : False si `new_safe_text` ne préfixe pas exactement `committed` — le
      jugement "sûr" d'un appel précédent a été contredit par ce nouvel appel, une violation
      de l'hypothèse de monotonie d'AlignAtt. Le texte déjà commité n'est jamais corrigé
      (cf. règle "le passé est immuable", `Loom/CLAUDE.md`) : l'appelant doit logger un
      WARNING dans ce cas, pas planter, mais aucun increment n'est retourné (`""`).
    """
    if not new_safe_text.startswith(committed):
        return "", False
    return new_safe_text[len(committed) :], True
