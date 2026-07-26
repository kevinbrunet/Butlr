from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import Iterator
    from pathlib import Path

    import numpy as np

MODEL_LANGUAGE = "french_24l"
FALLBACK_VOICE = "estelle"


class PocketTtsSynthesizer:
    """Synthèse FR via Pocket TTS (ADR-0036). Ce composant reste un client fin autour de
    `TTSModel` — le registre de voix par locuteur (pool de repli + profils personnalisés
    clonés, ADR-0046) est un travail d'orchestrateur (`voice_personalization.py`), pas de ce
    composant : il expose seulement les primitives (cloner/exporter/charger un état vocal),
    ne décide jamais lui-même quelle voix utiliser pour quel locuteur.

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
        self._voice = voice
        self._voice_state = self._model.get_state_for_audio_prompt(voice)
        self._named_voice_cache: dict[str, object] = {}

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

    def synthesize_stream(
        self, text: str, voice_state: object | None = None
    ) -> "Iterator[np.ndarray]":
        """Synthétise `text` (FR) en streaming : générateur de chunks audio (24kHz, cf.
        `sample_rate_hz`), pas un tenseur complet. Le délai avant le premier chunk produit
        (mesuré par l'appelant) est la métrique de budget réelle de ADR-0036 (p95 < 400ms),
        pas le temps total pour épuiser le générateur.

        `voice_state` : état vocal à utiliser pour cet appel (voix de pool ou profil
        personnalisé, cf. `voice_personalization.py`, ADR-0046) — `None` (défaut) retombe sur
        l'état de repli du constructeur (`self._voice_state`, historiquement `estelle`).

        Chaque appel repart de l'état vocal fourni (`copy_state` par défaut à `True` côté
        Pocket TTS) : deux appels successifs ne s'enchaînent pas naturellement (silence/rupture
        de prosodie entre les deux) — c'est délibéré depuis 2026-07-25 (cf. Révisions
        ADR-0041) : `new_line_state()`/`synthesize_continuation()` (`copy_state=False`,
        ci-dessous) chaînaient les increments d'une même ligne pour éviter cette rupture, mais
        ✓ constaté par exécution réelle sur la machine cible que ce chemin fait dégénérer
        Pocket TTS en boucle audio (un mot/une courte phrase répété des dizaines de secondes)
        — reproduit de façon quasi systématique en isolation (5/5 essais), y compris juste
        après un reset de l'état, et absent à 100% en rejouant la même séquence via
        `synthesize_stream()` (`copy_state=True`). Aucun usage de `copy_state=False` trouvé
        ailleurs dans l'écosystème Pocket TTS (ni les mainteneurs — issue
        kyutai-labs/pocket-tts#151, ni zeropointnine/tts-audiobook-tool) — chemin de code
        manifestement peu exercé. `new_line_state()`/`synthesize_continuation()` restent pour
        `bench/harness_tts_continuation.py` (sonde de régression) — ne plus les utiliser en
        production.
        """
        state = voice_state if voice_state is not None else self._voice_state
        for chunk in self._model.generate_audio_stream(state, text):
            yield chunk.numpy()

    def clone_voice_state(self, audio: "np.ndarray") -> object:
        """Construit un état vocal à partir d'un clip audio brut (mono, `sample_rate_hz`) —
        clonage de voix (ADR-0046), pas un des presets nommés du constructeur.

        ⚠ Non vérifié par exécution réelle (pas la machine cible) : `get_state_for_audio_prompt`
        accepte `Path | str | torch.Tensor` d'après la doc officielle (README
        kyutai-labs/pocket-tts, lu le 2026-07-25) — on lui passe donc un tenseur torch converti
        depuis `audio`, sans écrire de fichier intermédiaire. Format/plage de valeurs attendus
        (float32 [-1, 1], comme le reste de ce module) supposés identiques à `generate_audio`,
        pas confirmés spécifiquement pour ce chemin. Opération lente (cf. doc officielle,
        "relatively slow") — l'appelant doit passer par `asyncio.to_thread`, jamais depuis la
        boucle événementielle (même règle que `synthesize`/`synthesize_stream`).
        """
        import torch

        audio_tensor = torch.from_numpy(audio)
        return self._model.get_state_for_audio_prompt(audio_tensor)

    def export_voice_state(self, state: object, path: "Path") -> None:
        """Exporte `state` en `.safetensors` (`export_model_state`, ADR-0036) pour un
        rechargement rapide ultérieur via `load_voice_state`. ⚠ Non vérifié par exécution
        réelle.
        """
        from pocket_tts import export_model_state

        export_model_state(state, str(path))

    def load_voice_state(self, path: "Path") -> object:
        """Recharge un état vocal exporté par `export_voice_state`. ⚠ Non vérifié par
        exécution réelle."""
        return self._model.get_state_for_audio_prompt(str(path))

    def get_named_voice_state(self, name: str) -> object:
        """État vocal pour un preset nommé de Pocket TTS (ex. `alba`, `giovanni`... cf.
        `voice_personalization.FALLBACK_VOICE_POOL`) — mis en cache par nom, `estelle` (le nom
        du constructeur) n'a pas besoin d'un second chargement. `get_state_for_audio_prompt`
        est documenté comme une opération relativement lente (README officiel) — d'où le
        cache, chaque nom n'est chargé qu'une fois par process.
        """
        if name == self._voice:
            return self._voice_state
        cached = self._named_voice_cache.get(name)
        if cached is None:
            cached = self._model.get_state_for_audio_prompt(name)
            self._named_voice_cache[name] = cached
        return cached

    def new_line_state(self) -> object:
        """⚠ Ne plus utiliser en production (cf. Révisions ADR-0041, 2026-07-25) — conservée
        pour `bench/harness_tts_continuation.py` (sonde de régression sur le bug de boucle
        audio ci-dessous).

        Retourne une copie indépendante de l'état vocal initial, à réutiliser pour tous
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
        """⚠ Ne plus utiliser en production (cf. `new_line_state`) — fait dégénérer Pocket
        TTS en boucle audio, cf. Révisions ADR-0041.

        Synthétise `text` (FR) en continuant l'état vocal `state` (muté en place,
        `copy_state=False`) — l'audio s'enchaîne naturellement avec les increments
        précédents générés sur ce même `state` (cf. `new_line_state`). `state` doit venir de
        `new_line_state()`, jamais de l'état interne partagé de ce synthesizer.
        """
        for chunk in self._model.generate_audio_stream(state, text, copy_state=False):
            yield chunk.numpy()
