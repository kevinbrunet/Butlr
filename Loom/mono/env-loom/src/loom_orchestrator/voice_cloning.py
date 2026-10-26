from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

    from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# Clonage progressif du locuteur unique (ADR-0048) : la partie « clonage par palier » d'ADR-0046,
# sans ce qui servait à distinguer les locuteurs. En mono, tout l'audio d'entrée vient du même
# locuteur et ne se chevauche avec personne : il est « propre » par construction, sans
# séparation ni suivi d'identité. Pas de registre persisté non plus : il reconnaissait un
# locuteur d'une session à l'autre par l'empreinte vocale du suivi d'identité, absente ici.

SOURCE_SAMPLE_RATE_HZ = 16_000
FALLBACK_VOICE = "estelle"
# ⚠ Valeurs de départ non calibrées, reprises telles quelles d'ADR-0046 (`voice_registry.py`
# de multi) — à ajuster après écoute réelle.
TIER_LOW_S = 8.0
TIER_MEDIUM_S = 25.0
TIER_HD_S = 60.0


class VoiceTier(IntEnum):
    NONE = 0
    LOW = 1
    MEDIUM = 2
    HD = 3


def compute_tier(audio_seconds: float) -> VoiceTier:
    """Palier atteint pour `audio_seconds` d'audio source accumulé — pur."""
    if audio_seconds >= TIER_HD_S:
        return VoiceTier.HD
    if audio_seconds >= TIER_MEDIUM_S:
        return VoiceTier.MEDIUM
    if audio_seconds >= TIER_LOW_S:
        return VoiceTier.LOW
    return VoiceTier.NONE


@dataclass
class ProgressiveVoiceCloner:
    """Voix de repli (`estelle`, seule voix Pocket TTS confirmée FR) jusqu'au premier palier,
    puis voix clonée reconstruite à chaque palier franchi depuis tout l'audio accumulé
    (Pocket TTS n'a pas d'entraînement incrémental, cf. ADR-0046)."""

    synth: "PocketTtsSynthesizer"
    fallback_voice: str = FALLBACK_VOICE
    audio_chunks: list = field(default_factory=list)
    audio_seconds: float = 0.0
    tier: VoiceTier = VoiceTier.NONE
    _cloned_state: object | None = None

    def get_voice_state(self) -> object:
        if self._cloned_state is not None:
            return self._cloned_state
        return self.synth.get_named_voice_state(self.fallback_voice)

    def add_audio(self, audio: "np.ndarray") -> bool:
        """Accumule l'audio source (sans GPU, appelable depuis la boucle d'événements) ;
        retourne True si un nouveau palier vient d'être franchi, c'est-à-dire qu'il faut
        appeler `rebuild`. Séparé de `rebuild` pour que l'accumulation ne dépende pas du
        consommateur GPU : une première version accumulait dans le consommateur, à la
        priorité la plus basse, et n'y avait jamais accès (file de commits jamais vide)."""
        if self.tier is VoiceTier.HD:
            return False
        self.audio_chunks.append(audio)
        self.audio_seconds += len(audio) / SOURCE_SAMPLE_RATE_HZ
        new_tier = compute_tier(self.audio_seconds)
        if new_tier is self.tier:
            return False
        self.tier = new_tier
        return True

    def rebuild(self) -> None:
        """Bloquant (torch/Pocket TTS) — appeler via `asyncio.to_thread`, et depuis le même
        consommateur unique que la traduction et la synthèse (cf. Révisions ADR-0046 :
        segfault quand on parallélise les appels GPU). Reconstruit depuis tout l'audio
        accumulé à cet instant."""
        import numpy as np

        # Copie de la liste : la boucle d'événements continue d'y ajouter (`add_audio`)
        # pendant que ce thread la lit.
        audio = np.concatenate(list(self.audio_chunks))
        tier, seconds = self.tier, self.audio_seconds
        self._cloned_state = self.synth.clone_voice_state(audio, SOURCE_SAMPLE_RATE_HZ)
        print(
            f"Voix clonée reconstruite (palier={tier.name}, {seconds:.1f}s d'audio) — active "
            "à partir du prochain increment."
        )
        if tier is VoiceTier.HD:
            self.audio_chunks = []
