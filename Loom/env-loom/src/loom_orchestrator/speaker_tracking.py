from __future__ import annotations

# ⚠ Seuils non calibrés empiriquement (ADR-0042) — valeurs de départ, à ajuster une fois
# mesurées sur la machine cible.
STREAM_DISTINCT_THRESHOLD = 0.75
MATCH_CONFIDENCE_THRESHOLD = 0.5


def cosine_similarity(a: list[float], b: list[float]) -> float:
    """Similarité cosinus entre deux embeddings — proche de 1 si même locuteur probable,
    proche de 0 ou négatif sinon. Fonction pure, aucune dépendance numpy/torch (les
    embeddings arrivent déjà comme listes de floats, cf. `speaker_separation.py`).
    """
    dot = sum(x * y for x, y in zip(a, b))
    norm_a = sum(x * x for x in a) ** 0.5
    norm_b = sum(y * y for y in b) ** 0.5
    if norm_a == 0.0 or norm_b == 0.0:
        return 0.0
    return dot / (norm_a * norm_b)


def streams_are_distinct(
    stream_embeddings: list[list[float]], threshold: float = STREAM_DISTINCT_THRESHOLD
) -> bool:
    """True si les deux flux séparés semblent correspondre à des voix différentes — pas
    juste deux copies quasi identiques du mélange, signe qu'il n'y avait rien de réel à
    séparer (cf. ADR-0042, cas courant : un seul locuteur actif, pas de chevauchement).
    """
    if len(stream_embeddings) < 2:
        return False
    similarity = cosine_similarity(stream_embeddings[0], stream_embeddings[1])
    return similarity < threshold


def pick_matching_stream(
    reference_embedding: list[float], stream_embeddings: list[list[float]]
) -> tuple[int, float]:
    """Retourne `(index, similarité)` du flux séparé dont l'embedding est le plus proche de
    `reference_embedding` — soit l'embedding déjà sauvegardé pour ce locuteur, soit (au tout
    premier appel, sans embedding sauvegardé) l'embedding du mélange brut lui-même : comme
    WLK a déjà attribué ce segment à ce locuteur, le flux séparé le plus proche du mélange
    global est le candidat le plus probable pour être le locuteur dominant (cf. ADR-0042).
    """
    similarities = [cosine_similarity(reference_embedding, e) for e in stream_embeddings]
    best_index = similarities.index(max(similarities))
    return best_index, similarities[best_index]


def update_running_embedding(
    old: list[float] | None, new: list[float], count: int
) -> list[float]:
    """Moyenne mobile : affine l'embedding sauvegardé d'un locuteur (`old`, basé sur
    `count` échantillons déjà intégrés) avec une nouvelle observation (`new`). `old=None`
    (aucun embedding sauvegardé encore) retourne directement `new`.
    """
    if old is None or count == 0:
        return list(new)
    return [(o * count + n) / (count + 1) for o, n in zip(old, new)]
