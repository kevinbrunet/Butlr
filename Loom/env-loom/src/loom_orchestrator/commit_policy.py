from __future__ import annotations

# Version initiale d'ADR-0041 (segmentation ponctuation+pause), rejetée avant implémentation
# au profit d'AlignAtt (cf. ADR-0041 §Alternatives) — redevient pertinente avec ADR-0043 : un
# LLM decoder-only n'a pas le problème de coût qui avait motivé le rejet d'une politique à
# seuil fixe côté Seamless (ré-encodage complet par appel). Flush sur ponctuation forte (fin
# de phrase) ou virgule — le changement de locuteur reste géré ailleurs (`force_final_commit`
# dans `harness_pipeline.py`, déjà existant), ce module ne couvre que la segmentation à
# l'intérieur d'une ligne WLK encore active.
SENTENCE_END_CHARS = frozenset(".!?")
SOFT_PAUSE_CHARS = frozenset(",")
BOUNDARY_CHARS = SENTENCE_END_CHARS | SOFT_PAUSE_CHARS


def find_last_boundary(text: str) -> int | None:
    """Position (exclusive, juste après le caractère trouvé) du dernier point de
    segmentation (ponctuation forte ou virgule) dans `text`, en partant de la fin — ou
    `None` si aucun trouvé. On cherche le *dernier* point plutôt que le premier pour flush
    le plus de texte possible d'un coup (une ligne peut contenir plusieurs phrases
    complètes entre deux polls WLK)."""
    for i in range(len(text) - 1, -1, -1):
        if text[i] in BOUNDARY_CHARS:
            return i + 1
    return None


def compute_flush(full_text: str, already_flushed: str) -> tuple[str, str, bool]:
    """Étant donné `full_text` (texte complet accumulé pour une ligne WLK encore active) et
    `already_flushed` (préfixe déjà envoyé à la traduction lors d'un appel précédent),
    détermine s'il y a un nouveau segment à flush.

    Retourne `(segment, new_already_flushed, is_consistent)` :
    - `is_consistent=False` si `full_text` ne préfixe pas `already_flushed` — WLK a révisé
      du texte déjà flushé (`lines` n'est pas append-only, cf. ADR-0039). `segment` est vide
      et `new_already_flushed` vaut l'ancien `already_flushed` inchangé : l'appelant doit
      logger un WARNING et ignorer cet appel, jamais corriger un segment déjà envoyé à la
      traduction (cf. "le passé est immuable", `Loom/CLAUDE.md`) — même politique que
      `alignatt.compute_increment` pour la même raison.
    - Si cohérent mais qu'aucun nouveau point de segmentation n'est apparu dans la partie
      non encore flushée, `segment` est vide et `new_already_flushed` égale `already_flushed`
      — rien à faire pour l'instant, ce n'est pas une erreur.
    - Si cohérent et qu'un nouveau point de segmentation est trouvé, `segment` est le texte
      entre `already_flushed` et ce point (strippé), et `new_already_flushed` est mis à jour
      jusqu'à ce point — le reste (après le point) attend le prochain appel.
    """
    if not full_text.startswith(already_flushed):
        return "", already_flushed, False

    unflushed = full_text[len(already_flushed) :]
    boundary = find_last_boundary(unflushed)
    if boundary is None:
        return "", already_flushed, True

    segment = unflushed[:boundary].strip()
    new_already_flushed = already_flushed + unflushed[:boundary]
    if not segment:
        return "", new_already_flushed, True
    return segment, new_already_flushed, True


def force_flush(full_text: str, already_flushed: str) -> tuple[str, str, bool]:
    """Comme `compute_flush`, mais sans attendre de point de segmentation — flush tout le
    texte non encore flushé tel quel. Pour le scellement définitif d'une ligne WLK (fin de
    tour de parole ou changement de locuteur) : l'audio ne grandira plus, il n'y a pas de
    raison d'attendre une ponctuation qui ne viendra peut-être jamais (cf.
    `force_final_commit` dans `harness_pipeline.py`, même rôle que pour AlignAtt)."""
    if not full_text.startswith(already_flushed):
        return "", already_flushed, False

    segment = full_text[len(already_flushed) :].strip()
    if not segment:
        return "", full_text, True
    return segment, full_text, True
