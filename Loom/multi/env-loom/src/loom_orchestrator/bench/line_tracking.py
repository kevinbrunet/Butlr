from __future__ import annotations


def extract_updates(
    lines: list[dict],
    known_texts: list[str],
) -> list[tuple[int, dict, str]]:
    """Compare l'état courant de `lines` (WLK, mode "full") à `known_texts` (dernier texte vu
    par index) et retourne les lignes dont le texte a changé — nouvelle ligne ou texte étendu.

    ⚠ Constaté empiriquement en T1.1 (pas une simple lecture de code) : WLK ne renvoie pas des
    lignes figées une fois commitées — le texte d'un index existant continue de grandir sur de
    nombreux polls successifs (une phrase entière peut rester à l'index 0 pendant >60s). Un
    suivi naïf par `len(lines)` seul (première version du harnais) ratait silencieusement toute
    cette croissance : un seul événement de latence était loggué pour tout un run. `known_texts`
    est mutée en place (extension pour les nouveaux index, mise à jour du texte vu) pour que
    l'appelant n'ait qu'à conserver cette liste d'un poll à l'autre.
    """
    updates: list[tuple[int, dict, str]] = []
    for idx, line in enumerate(lines):
        text = line.get("translation") or line.get("text") or ""
        if idx >= len(known_texts):
            known_texts.append("")
        if text and text != known_texts[idx]:
            updates.append((idx, line, text))
            known_texts[idx] = text
    return updates
