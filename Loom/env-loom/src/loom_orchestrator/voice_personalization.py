from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import TYPE_CHECKING

from loom_orchestrator.tts_pocket import PocketTtsSynthesizer
from loom_orchestrator.voice_registry import (
    VoiceProfileRecord,
    VoiceRegistry,
    VoiceTier,
    compute_tier,
)

if TYPE_CHECKING:
    from pathlib import Path

    import numpy as np

# Liste complète des voix prédéfinies Pocket TTS (README officiel kyutai-labs/pocket-tts, lu
# le 2026-07-25) — `estelle` en premier (seule confirmée FR), le reste couvre EN/IT/ES/DE/PT.
# ⚠ Qualité de synthèse FR avec ces presets non-FR non vérifiée (clonage cross-lingue,
# jamais testé dans Loom, cf. ADR-0046) — pool complet choisi explicitement par Kevin plutôt
# que `estelle` seule, pour maximiser la distinction entre locuteurs avant personnalisation,
# malgré ce risque de qualité.
FALLBACK_VOICE_POOL = [
    "estelle",
    "alba",
    "anna",
    "azelma",
    "bill_boerst",
    "caro_davy",
    "charles",
    "cosette",
    "eponine",
    "eve",
    "fantine",
    "george",
    "jane",
    "jean",
    "javert",
    "marius",
    "mary",
    "michael",
    "paul",
    "peter_yearsley",
    "stuart_bell",
    "vera",
    "giovanni",
    "lola",
    "juergen",
    "rafael",
]

# ⚠ Garde-fou simple (pas d'éviction LRU en v1, cf. ADR-0046 "Alternatives considérées") —
# au-delà, les nouvelles identités restent sur leur voix de pool tant qu'une place ne se
# libère pas (jamais implémenté en v1 — le plafond est dur). Rappel du risque : un état vocal
# jamais libéré a déjà causé une fuite VRAM de 13,9 → 29,85 Go dans ce projet (Révisions
# ADR-0042/0036) ; les états personnalisés vivent ici pour toute la durée du run (pas juste
# une ligne comme l'ancien bug), d'où ce plafond dès la première version.
MAX_LOADED_PERSONALIZED_VOICES = 8


def pool_voice_name_for(ident: int, pool: list[str] = FALLBACK_VOICE_POOL) -> str:
    """Assignation round-robin déterministe d'une voix de pool à une identité — fonction
    pure, testable sans Pocket TTS."""
    return pool[ident % len(pool)]


def _iso_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _write_wav_mono(path: "Path", audio: "np.ndarray", sample_rate_hz: int) -> None:
    """PCM16 mono — même format que `bench/harness_tts_continuation._write_wav`, ré-écrit
    localement plutôt qu'importé : `loom_orchestrator/` (production) ne doit pas dépendre de
    `bench/` (outillage de mesure), sens de dépendance déjà identifié comme un bug une fois
    dans ce repo (cf. docstring de relocalisation de `commit_state.LineCommitState`)."""
    import wave

    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


def _read_wav_mono(path: "Path", expected_sample_rate_hz: int) -> "np.ndarray":
    """Inverse de `_write_wav_mono` — PCM16 mono vers float32 `[-1, 1]`, même convention que
    `main.py:feed()` pour l'audio entrant."""
    import wave

    import numpy as np

    with wave.open(str(path), "rb") as wav_file:
        if wav_file.getframerate() != expected_sample_rate_hz:
            raise ValueError(
                f"{path} : fréquence d'échantillonnage {wav_file.getframerate()}Hz, "
                f"attendue {expected_sample_rate_hz}Hz"
            )
        pcm16 = np.frombuffer(wav_file.readframes(wav_file.getnframes()), dtype=np.int16)
    return pcm16.astype(np.float32) / 32768.0


@dataclass
class _PersonalizationState:
    """État en mémoire pour une identité en cours de personnalisation. `speaker_key` démarre
    aléatoire (`uuid`) et est remplacé par la clé d'un profil déjà connu si le registre
    reconnaît ce locuteur (cf. `PersonalizedVoiceManager.on_clean_audio`)."""

    speaker_key: str = field(default_factory=lambda: uuid.uuid4().hex[:12])
    audio_chunks: list = field(default_factory=list)
    audio_seconds: float = 0.0
    tier: VoiceTier = VoiceTier.NONE
    registry_checked: bool = False


class PersonalizedVoiceManager:
    """Orchestre la personnalisation de voix par locuteur (ADR-0046, T3.1-T3.3) :

    1. Pool de repli (`assign_fallback`) assigné dès la création d'une identité, pour la
       distinguer des autres avant toute personnalisation.
    2. Reconnaissance d'un locuteur déjà rencontré (session courante ou précédente) via le
       registre persisté (`voice_registry.VoiceRegistry`), sur l'embedding déjà calculé pour
       le suivi d'identité (ADR-0042/0044) — pas un second système de reconnaissance.
    3. Accumulation d'audio **propre** (sans chevauchement — c'est l'appelant qui garantit ça
       en n'appelant `on_clean_audio` que depuis la branche adéquate de `route_window`) et
       reconstruction de l'état vocal à chaque palier franchi (`voice_registry.VoiceTier`).
       "Affiner avec le temps" ici veut dire *reconstruire depuis un prompt audio plus long*
       (Pocket TTS n'a pas d'API d'entraînement incrémental), pas ajuster des poids.

    ⚠ Ne borne aucune notion de "propreté" de l'audio elle-même — un appelant qui alimente
    `on_clean_audio` avec de l'audio contaminé (chevauchement, séparation aveugle) produira un
    profil de mauvaise qualité sans erreur explicite.
    """

    def __init__(self, synth: PocketTtsSynthesizer, registry: VoiceRegistry) -> None:
        self._synth = synth
        self._registry = registry
        self._pool_voices: dict[int, str] = {}
        self._personal_states: dict[int, object] = {}
        self._personalization: dict[int, _PersonalizationState] = {}

    def assign_fallback(self, ident: int) -> None:
        """À appeler une fois, à la création d'une identité (`main.py:_ensure_identity`)."""
        self._pool_voices[ident] = pool_voice_name_for(ident)

    def get_voice_state(self, ident: int) -> object | None:
        """État vocal à utiliser pour `ident` — profil personnalisé si construit, sinon la
        voix de pool assignée à la création, sinon `None` (repli du constructeur de
        `PocketTtsSynthesizer` — ne devrait arriver que si `assign_fallback` n'a jamais été
        appelé pour cette identité, signe d'un bug côté appelant)."""
        personal = self._personal_states.get(ident)
        if personal is not None:
            return personal
        pool_voice = self._pool_voices.get(ident)
        if pool_voice is not None:
            return self._synth.get_named_voice_state(pool_voice)
        return None

    def on_clean_audio(self, ident: int, embedding: list[float], audio: "np.ndarray") -> None:
        """Bloquant (torch/Pocket TTS) — appeler via `asyncio.to_thread`, jamais depuis la
        boucle événementielle (même règle que le reste des appels Pocket TTS de ce repo).

        À appeler uniquement pour de l'audio source sans chevauchement (cf. docstring de
        classe) — jamais pour un flux issu de la séparation de voix.
        """
        state = self._personalization.get(ident)
        if state is None:
            state = _PersonalizationState()
            self._personalization[ident] = state

        at_capacity = (
            ident not in self._personal_states
            and len(self._personal_states) >= MAX_LOADED_PERSONALIZED_VOICES
        )

        if not state.registry_checked:
            state.registry_checked = True
            match = self._registry.find_matching(embedding)
            if match is not None:
                state.speaker_key = match.speaker_key
                state.tier = match.tier
                state.audio_seconds = match.audio_seconds
                raw_path = self._registry.raw_audio_path(match.speaker_key)
                if raw_path.exists():
                    state.audio_chunks = [
                        _read_wav_mono(raw_path, self._synth.sample_rate_hz)
                    ]
                if not at_capacity:
                    self._load_personal_state(ident, match.speaker_key)
                return

        if state.tier is VoiceTier.HD:
            return  # palier maximal déjà atteint, pas besoin de plus d'audio

        state.audio_chunks.append(audio)
        state.audio_seconds += len(audio) / self._synth.sample_rate_hz

        new_tier = compute_tier(state.audio_seconds)
        if new_tier is state.tier:
            return
        state.tier = new_tier
        if at_capacity:
            return  # audio gardé en mémoire pour plus tard, pas de construction GPU
        self._rebuild_voice_state(ident, state, embedding)

    def _load_personal_state(self, ident: int, speaker_key: str) -> bool:
        safetensors_path = self._registry.safetensors_path(speaker_key)
        if not safetensors_path.exists():
            return False
        self._personal_states[ident] = self._synth.load_voice_state(safetensors_path)
        return True

    def _rebuild_voice_state(
        self, ident: int, state: _PersonalizationState, embedding: list[float]
    ) -> None:
        import numpy as np

        full_audio = np.concatenate(state.audio_chunks)
        voice_state = self._synth.clone_voice_state(full_audio)
        self._personal_states[ident] = voice_state

        self._registry.directory.mkdir(parents=True, exist_ok=True)
        self._synth.export_voice_state(
            voice_state, self._registry.safetensors_path(state.speaker_key)
        )
        _write_wav_mono(
            self._registry.raw_audio_path(state.speaker_key),
            full_audio,
            self._synth.sample_rate_hz,
        )
        self._registry.upsert(
            VoiceProfileRecord(
                speaker_key=state.speaker_key,
                embedding=embedding,
                tier=state.tier,
                audio_seconds=state.audio_seconds,
                updated_at=_iso_now(),
            )
        )
