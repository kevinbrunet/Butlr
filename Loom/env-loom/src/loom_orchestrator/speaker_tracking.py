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


def find_best_speaker(
    known_speakers: list[list[float]],
    stream_embedding: list[float],
    threshold: float = MATCH_CONFIDENCE_THRESHOLD,
) -> tuple[int | None, float]:
    """Cherche le locuteur déjà connu le plus proche de `stream_embedding` dans un
    référentiel **ouvert** (pas limité à un nombre fixe d'identités, cf. ADR-0044 §Révisions
    2026-07-18 — remarque de Kevin : rejeter une correspondance incertaine faisait perdre le
    flux entier plutôt que de risquer une erreur ponctuelle, préférer se tromper rarement
    qu'ignorer). Retourne `(index, similarité)` si un locuteur connu dépasse `threshold`,
    sinon `(None, meilleure_similarité)` — l'appelant doit alors enregistrer un nouveau
    locuteur plutôt que d'ignorer l'incrément.
    """
    if not known_speakers:
        return None, 0.0
    similarities = [cosine_similarity(k, stream_embedding) for k in known_speakers]
    best_index = similarities.index(max(similarities))
    best_similarity = similarities[best_index]
    if best_similarity >= threshold:
        return best_index, best_similarity
    return None, best_similarity


def assign_streams_open_set(
    known_speakers: list[list[float]],
    stream_embeddings: list[list[float]],
    threshold: float = MATCH_CONFIDENCE_THRESHOLD,
) -> list[int]:
    """Assigne chaque flux séparé d'une même fenêtre à un locuteur — un locuteur déjà connu
    (index dans `known_speakers`) ou un **nouveau** locuteur (index `>= len(known_speakers)`,
    un par flux non reconnu, dans l'ordre des flux). Remplace `assign_and_bootstrap`
    (2 identités fixes, ADR-0044 conception initiale) : référentiel ouvert, pas de plafond
    sur le nombre de locuteurs distincts au fil d'une session (PixIT sépare jusqu'à 3 voix
    *simultanées* par fenêtre, mais la pièce peut contenir plus de 3 personnes qui parlent
    chacune leur tour) — et surtout **jamais de rejet** : un flux qui ne correspond à
    personne devient un nouveau locuteur plutôt que d'être ignoré (cf. `find_best_speaker`).

    Assignation gloutonne par similarité décroissante sur toutes les paires (flux, locuteur
    connu) : chaque flux et chaque locuteur connu n'est utilisé qu'une fois, pour éviter que
    deux flux distincts de la même fenêtre revendiquent le même locuteur connu.
    """
    n_streams = len(stream_embeddings)
    assignment: list[int | None] = [None] * n_streams

    candidates = sorted(
        (
            (cosine_similarity(known_speakers[k], stream_embeddings[s]), s, k)
            for s in range(n_streams)
            for k in range(len(known_speakers))
        ),
        key=lambda c: c[0],
        reverse=True,
    )
    used_streams: set[int] = set()
    used_known: set[int] = set()
    for similarity, s, k in candidates:
        if similarity < threshold:
            break
        if s in used_streams or k in used_known:
            continue
        assignment[s] = k
        used_streams.add(s)
        used_known.add(k)

    next_new = len(known_speakers)
    for s in range(n_streams):
        if assignment[s] is None:
            assignment[s] = next_new
            next_new += 1
    return assignment  # type: ignore[return-value]
