from __future__ import annotations

from pathlib import Path

from loom_orchestrator.voice_registry import (
    TIER_HD_S,
    TIER_LOW_S,
    TIER_MEDIUM_S,
    VoiceProfileRecord,
    VoiceRegistry,
    VoiceTier,
    compute_tier,
    deserialize_manifest,
    find_matching_profile,
    serialize_manifest,
)


def _record(speaker_key: str, embedding: list[float], tier: VoiceTier = VoiceTier.LOW) -> VoiceProfileRecord:
    return VoiceProfileRecord(
        speaker_key=speaker_key,
        embedding=embedding,
        tier=tier,
        audio_seconds=TIER_LOW_S,
        updated_at="2026-07-26T00:00:00",
    )


def test_compute_tier_none_below_low_threshold() -> None:
    assert compute_tier(0.0) is VoiceTier.NONE
    assert compute_tier(TIER_LOW_S - 0.1) is VoiceTier.NONE


def test_compute_tier_low_at_threshold() -> None:
    assert compute_tier(TIER_LOW_S) is VoiceTier.LOW
    assert compute_tier(TIER_MEDIUM_S - 0.1) is VoiceTier.LOW


def test_compute_tier_medium_at_threshold() -> None:
    assert compute_tier(TIER_MEDIUM_S) is VoiceTier.MEDIUM
    assert compute_tier(TIER_HD_S - 0.1) is VoiceTier.MEDIUM


def test_compute_tier_hd_at_and_above_threshold() -> None:
    assert compute_tier(TIER_HD_S) is VoiceTier.HD
    assert compute_tier(TIER_HD_S * 10) is VoiceTier.HD


def test_find_matching_profile_returns_closest_above_threshold() -> None:
    profiles = [_record("a", [1.0, 0.0]), _record("b", [0.0, 1.0])]
    match = find_matching_profile([0.99, 0.01], profiles)
    assert match is not None
    assert match.speaker_key == "a"


def test_find_matching_profile_returns_none_below_threshold() -> None:
    profiles = [_record("a", [1.0, 0.0])]
    match = find_matching_profile([0.0, 1.0], profiles)
    assert match is None


def test_find_matching_profile_returns_none_for_empty_registry() -> None:
    assert find_matching_profile([1.0, 0.0], []) is None


def test_manifest_round_trip() -> None:
    profiles = [_record("a", [1.0, 0.0], VoiceTier.HD), _record("b", [0.0, 1.0], VoiceTier.MEDIUM)]
    restored = deserialize_manifest(serialize_manifest(profiles))
    assert restored == profiles


def test_registry_upsert_persists_and_replaces(tmp_path: Path) -> None:
    registry = VoiceRegistry.load(tmp_path)
    registry.upsert(_record("a", [1.0, 0.0], VoiceTier.LOW))
    registry.upsert(_record("a", [1.0, 0.0], VoiceTier.HD))

    assert len(registry.profiles) == 1
    assert registry.profiles[0].tier is VoiceTier.HD

    reloaded = VoiceRegistry.load(tmp_path)
    assert len(reloaded.profiles) == 1
    assert reloaded.profiles[0].tier is VoiceTier.HD


def test_registry_load_missing_manifest_returns_empty(tmp_path: Path) -> None:
    registry = VoiceRegistry.load(tmp_path / "does-not-exist-yet")
    assert registry.profiles == []


def test_registry_paths_are_scoped_to_directory(tmp_path: Path) -> None:
    registry = VoiceRegistry.load(tmp_path)
    assert registry.raw_audio_path("a") == tmp_path / "a.raw.wav"
    assert registry.safetensors_path("a") == tmp_path / "a.safetensors"
