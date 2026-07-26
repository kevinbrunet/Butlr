from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from enum import IntEnum
from pathlib import Path

from loom_orchestrator.speaker_tracking import MATCH_CONFIDENCE_THRESHOLD, cosine_similarity

# Résolu relativement à ce fichier (pas au CWD) : `env-loom/src/loom_orchestrator/
# voice_registry.py` -> `env-loom/voice_profiles/` — même leçon que le bug `CORPUS_DIR`
# déjà corrigé dans ce repo (un chemin nu supposait un lancement depuis une racine
# particulière, source récurrente de `FileNotFoundError`).
VOICE_PROFILES_DIR = Path(__file__).resolve().parents[2] / "voice_profiles"
MANIFEST_FILENAME = "manifest.json"

# ⚠ Valeurs de départ non calibrées (ADR-0046) — Kevin n'a pas de repère connu sur la durée
# d'audio propre nécessaire par palier. ADR-0036 mentionne "~5s" pour un clonage basique,
# sans source ferme. À ajuster après écoute réelle sur la machine cible.
TIER_LOW_S = 8.0
TIER_MEDIUM_S = 25.0
TIER_HD_S = 60.0


class VoiceTier(IntEnum):
    """Palier de qualité d'un profil de voix personnalisé, croissant avec la durée d'audio
    propre (sans chevauchement) accumulée pour ce locuteur — cf. `compute_tier`. `NONE`
    signifie qu'aucun profil personnalisé n'existe encore pour ce locuteur (voix de pool
    utilisée à la place, cf. `voice_personalization.py`).
    """

    NONE = 0
    LOW = 1
    MEDIUM = 2
    HD = 3


def compute_tier(audio_seconds: float) -> VoiceTier:
    """Palier atteint pour `audio_seconds` d'audio propre accumulé — fonction pure, testable
    sans dépendance à Pocket TTS.
    """
    if audio_seconds >= TIER_HD_S:
        return VoiceTier.HD
    if audio_seconds >= TIER_MEDIUM_S:
        return VoiceTier.MEDIUM
    if audio_seconds >= TIER_LOW_S:
        return VoiceTier.LOW
    return VoiceTier.NONE


@dataclass
class VoiceProfileRecord:
    """Entrée du registre de voix personnalisées — une par locuteur reconnu. `embedding` est
    ce qui permet de reconnaître ce locuteur d'une session à l'autre (même embedding que le
    suivi d'identité en direct, cf. `speaker_tracking.py`/`main.py`), pas un identifiant
    stable côté utilisateur. `speaker_key` sert uniquement de nom de fichier
    (`<speaker_key>.raw.wav`/`.safetensors`, cf. `VoiceRegistry`).
    """

    speaker_key: str
    embedding: list[float]
    tier: VoiceTier
    audio_seconds: float
    updated_at: str

    def to_dict(self) -> dict:
        data = asdict(self)
        data["tier"] = int(self.tier)
        return data

    @staticmethod
    def from_dict(data: dict) -> "VoiceProfileRecord":
        return VoiceProfileRecord(
            speaker_key=data["speaker_key"],
            embedding=list(data["embedding"]),
            tier=VoiceTier(data["tier"]),
            audio_seconds=float(data["audio_seconds"]),
            updated_at=data["updated_at"],
        )


def find_matching_profile(
    embedding: list[float],
    profiles: list[VoiceProfileRecord],
    threshold: float = MATCH_CONFIDENCE_THRESHOLD,
) -> VoiceProfileRecord | None:
    """Cherche, parmi les profils déjà enregistrés, celui dont l'embedding est le plus proche
    de `embedding` — même seuil que le suivi d'identité en direct (`speaker_tracking.
    MATCH_CONFIDENCE_THRESHOLD`), pour rester cohérent plutôt que d'inventer un second seuil
    non calibré. Retourne `None` si aucun profil ne dépasse le seuil (nouveau locuteur, pas
    encore de profil).
    """
    if not profiles:
        return None
    best = max(profiles, key=lambda p: cosine_similarity(p.embedding, embedding))
    if cosine_similarity(best.embedding, embedding) >= threshold:
        return best
    return None


def serialize_manifest(profiles: list[VoiceProfileRecord]) -> dict:
    return {"profiles": [p.to_dict() for p in profiles]}


def deserialize_manifest(data: dict) -> list[VoiceProfileRecord]:
    return [VoiceProfileRecord.from_dict(d) for d in data.get("profiles", [])]


@dataclass
class VoiceRegistry:
    """Registre de voix personnalisées, persisté sur disque (ADR-0046) — un manifeste JSON
    (`manifest.json`, embeddings + métadonnées) plus, par locuteur, un `.raw.wav` (audio
    propre accumulé, conservé pour permettre de reprendre l'accumulation d'une session à
    l'autre — cf. docstring de `voice_personalization.py` sur ce que "affiner avec le temps"
    veut dire ici) et un `.safetensors` (état vocal Pocket TTS dérivé, rechargeable
    rapidement, cf. ADR-0036).

    ⚠ Contient de l'audio vocal identifiable de vraies personnes — `VOICE_PROFILES_DIR` ne
    doit jamais être commité (cf. `.gitignore`). Rétention/suppression long terme pas
    tranchée, hors scope POC.
    """

    directory: Path = field(default_factory=lambda: VOICE_PROFILES_DIR)
    profiles: list[VoiceProfileRecord] = field(default_factory=list)

    @classmethod
    def load(cls, directory: Path | None = None) -> "VoiceRegistry":
        resolved_dir = directory if directory is not None else VOICE_PROFILES_DIR
        registry = cls(directory=resolved_dir)
        manifest_path = resolved_dir / MANIFEST_FILENAME
        if manifest_path.exists():
            registry.profiles = deserialize_manifest(
                json.loads(manifest_path.read_text(encoding="utf-8"))
            )
        return registry

    def save(self) -> None:
        self.directory.mkdir(parents=True, exist_ok=True)
        manifest_path = self.directory / MANIFEST_FILENAME
        manifest_path.write_text(
            json.dumps(serialize_manifest(self.profiles), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )

    def find_matching(self, embedding: list[float]) -> VoiceProfileRecord | None:
        return find_matching_profile(embedding, self.profiles)

    def raw_audio_path(self, speaker_key: str) -> Path:
        return self.directory / f"{speaker_key}.raw.wav"

    def safetensors_path(self, speaker_key: str) -> Path:
        return self.directory / f"{speaker_key}.safetensors"

    def upsert(self, record: VoiceProfileRecord) -> None:
        """Remplace l'entrée existante pour `record.speaker_key` (ou l'ajoute) et persiste
        immédiatement le manifeste — pas de fenêtre où le registre en mémoire et le fichier
        divergent."""
        self.profiles = [p for p in self.profiles if p.speaker_key != record.speaker_key]
        self.profiles.append(record)
        self.save()
