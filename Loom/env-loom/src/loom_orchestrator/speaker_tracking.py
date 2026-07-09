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


def assign_streams_to_identities(
    known_embeddings: list[list[float]], stream_embeddings: list[list[float]]
) -> list[int]:
    """Associe chaque identité déjà connue (`known_embeddings`, longueur 2) au flux séparé
    (`stream_embeddings`, longueur 2) qui lui correspond le mieux (ADR-0044, séparation en
    amont de WLK) — choisit la permutation qui maximise la similarité totale, plutôt que
    deux appels indépendants à `pick_matching_stream` qui pourraient assigner le même flux
    aux deux identités. Ne gère que 2 identités/2 flux (POC à 2 locuteurs) — pas généralisé
    à N.

    Retourne `[index_du_flux_pour_identité_0, index_du_flux_pour_identité_1]`.
    """
    straight = cosine_similarity(known_embeddings[0], stream_embeddings[0]) + cosine_similarity(
        known_embeddings[1], stream_embeddings[1]
    )
    swapped = cosine_similarity(known_embeddings[0], stream_embeddings[1]) + cosine_similarity(
        known_embeddings[1], stream_embeddings[0]
    )
    return [0, 1] if straight >= swapped else [1, 0]


def assign_and_bootstrap(
    known_embeddings: list[list[float] | None], stream_embeddings: list[list[float]]
) -> list[int]:
    """Associe les 2 flux séparés aux 2 identités, y compris pendant le bootstrap avant que
    les deux identités aient un embedding sauvegardé (ADR-0044, séparation en amont de WLK —
    contrairement à ADR-0042 où WLK avait déjà attribué le segment à un locuteur, ici rien
    n'est connu au tout début du flux). Combine `assign_streams_to_identities` (cas normal,
    les deux identités déjà connues) avec deux cas de bootstrap :
    - aucune identité connue encore (chevauchement détecté avant que quiconque n'ait parlé
      seul) : assignation arbitraire `[0, 1]`.
    - une seule identité connue (cas le plus courant en pratique : un locuteur a parlé seul
      avant que l'autre ne rejoigne) : le flux le plus proche de l'identité déjà connue lui
      est assigné, l'autre flux devient la nouvelle identité.

    Retourne `[index_du_flux_pour_identité_0, index_du_flux_pour_identité_1]`.
    """
    known_indices = [i for i, e in enumerate(known_embeddings) if e is not None]
    if not known_indices:
        return [0, 1]
    if len(known_indices) == 1:
        known_idx = known_indices[0]
        other_idx = 1 - known_idx
        matched_stream, _ = pick_matching_stream(known_embeddings[known_idx], stream_embeddings)
        assignment = [0, 0]
        assignment[known_idx] = matched_stream
        assignment[other_idx] = 1 - matched_stream
        return assignment
    return assign_streams_to_identities(
        [known_embeddings[0], known_embeddings[1]], stream_embeddings
    )


def pick_active_identity(
    known_embeddings: list[list[float] | None], mixture_embedding: list[float]
) -> int:
    """Choisit l'identité active quand un seul locuteur parle (`streams_are_distinct` faux,
    ADR-0044) — similarité de l'embedding du mélange brut contre chaque identité déjà connue.
    Si aucune identité n'est encore connue (tout début de flux, avant toute détection de
    chevauchement), retourne l'identité 0 par convention (cf. ADR-0044 §Decision, bootstrap).
    """
    known = [(i, e) for i, e in enumerate(known_embeddings) if e is not None]
    if not known:
        return 0
    similarities = [(i, cosine_similarity(e, mixture_embedding)) for i, e in known]
    return max(similarities, key=lambda pair: pair[1])[0]
