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
        """
        for chunk in self._model.generate_audio_stream(self._voice_state, text):
            yield chunk.numpy()
