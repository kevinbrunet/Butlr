from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import Iterator

    import numpy as np

MODEL_LANGUAGE = "french_24l"
FALLBACK_VOICE = "estelle"


class PocketTtsSynthesizer:
    """Synthèse FR via Pocket TTS (ADR-0036) — Phase 1 : une seule voix FR de repli
    (`estelle`), pas de clonage par locuteur (T3.1-T3.3 pas commencés, aucun `.safetensors`
    exporté à ce jour). Le vrai registre de voix par `speaker_id` est un travail
    d'orchestrateur (T2.3/T3.x), pas de ce composant.

    ✓ Constaté par exécution réelle (premier run T2.3-préliminaire, 2026-07-15) : il n'existe
    pas de variante FR plus petite que 24 couches — `TTSModel.load_model(language="french")`
    échoue avec `ValueError: For technical reasons, only a larger 24-layer model is available
    for French. Please use the 'french_24l' language instead.` Corrige à la fois l'hypothèse
    initiale de ADR-0036 ("6 couches") et sa révision du 2026-07-15 ("12 couches par défaut",
    déduite de la doc officielle sans exécution réelle — la doc ne précisait pas que le FR
    n'a pas de variante par défaut plus légère). ✓ Le nom court `"estelle"` passé à
    `get_state_for_audio_prompt()` est confirmé par exécution réelle (chargement et synthèse
    réussis sans erreur).

    ⚠ Constaté par exécution réelle (`bench/harness_tts.py`, 2026-07-15) : `synthesize()`
    (`generate_audio`, bloquant jusqu'à la fin de l'énoncé) prend ~300-450ms **par mot**, pas
    un coût fixe — 7s sur une phrase de 22 mots. Mauvais choix d'API pour le budget de
    ADR-0036, qui porte sur le *time-to-first-chunk* (streaming), pas le temps de synthèse
    total d'un énoncé complet. `synthesize_stream()` (`generate_audio_stream`) est la bonne
    API pour mesurer/exploiter ce budget.

    ⚠ `TTSModel.generate_audio()`/`generate_audio_stream()` sont bloquants/CPU — ne jamais les
    appeler directement depuis la boucle asyncio (cf. `Loom/CLAUDE.md`, "ne pas laisser
    l'inférence Pocket TTS tourner dans la boucle événementielle") : l'appelant doit passer
    par `asyncio.to_thread` (ou itérer `synthesize_stream()` depuis un thread dédié).
    """

    def __init__(self, language: str = MODEL_LANGUAGE, voice: str = FALLBACK_VOICE) -> None:
        from pocket_tts import TTSModel

        self._model = TTSModel.load_model(language=language)
        self._voice_state = self._model.get_state_for_audio_prompt(voice)

    @property
    def sample_rate_hz(self) -> int:
        return self._model.sample_rate

    def synthesize(self, text: str) -> "np.ndarray":
        """Synthétise `text` (FR) et retourne l'audio complet (tenseur 1D) — bloque jusqu'à
        la fin de l'énoncé. ⚠ Ne mesure/n'exploite pas le budget time-to-first-chunk de
        ADR-0036 — cf. `synthesize_stream()`.
        """
        audio_tensor = self._model.generate_audio(self._voice_state, text)
        return audio_tensor.numpy()

    def synthesize_stream(self, text: str) -> "Iterator[np.ndarray]":
        """Synthétise `text` (FR) en streaming : générateur de chunks audio (24kHz, cf.
        `sample_rate_hz`), pas un tenseur complet. Le délai avant le premier chunk produit
        (mesuré par l'appelant) est la métrique de budget réelle de ADR-0036 (p95 < 400ms),
        pas le temps total pour épuiser le générateur.

        Chaque appel repart de l'état vocal initial (`copy_state` par défaut à `True` côté
        Pocket TTS) : deux appels successifs ne s'enchaînent pas naturellement (silence/rupture
        de prosodie entre les deux). Pour une ligne qui reçoit plusieurs increments successifs
        (cf. ADR-0041, `bench/harness_pipeline.py`), utiliser `new_line_state()` +
        `synthesize_continuation()` à la place.
        """
        for chunk in self._model.generate_audio_stream(self._voice_state, text):
            yield chunk.numpy()

    def new_line_state(self) -> object:
        """Retourne une copie indépendante de l'état vocal initial, à réutiliser pour tous
        les increments d'une même ligne (cf. `synthesize_continuation`) — jamais partagée
        entre deux lignes/tours de parole différents.

        ✓ Constaté par lecture du code source (`pocket_tts/models/tts_model.py`, lu le
        2026-07-15, non exécuté sur cette machine — pas la cible) : `generate_audio_stream`
        fait `model_state = copy.deepcopy(model_state)` seulement si `copy_state=True`
        (le défaut). Avec `copy_state=False`, la génération mute l'état en place (compteur de
        position interne) — des appels successifs sur le **même** objet `state` s'enchaînent
        comme un seul énoncé continu (continuité acoustique/prosodique), au lieu de repartir
        de zéro à chaque appel. D'où la nécessité d'une copie initiale dédiée par ligne : on
        ne veut la continuité *qu'au sein* d'une ligne, jamais entre deux lignes.
        """
        import copy

        return copy.deepcopy(self._voice_state)

    def synthesize_continuation(self, state: object, text: str) -> "Iterator[np.ndarray]":
        """Synthétise `text` (FR) en continuant l'état vocal `state` (muté en place,
        `copy_state=False`) — l'audio s'enchaîne naturellement avec les increments
        précédents générés sur ce même `state` (cf. `new_line_state`). `state` doit venir de
        `new_line_state()`, jamais de l'état interne partagé de ce synthesizer.
        """
        for chunk in self._model.generate_audio_stream(state, text, copy_state=False):
            yield chunk.numpy()
