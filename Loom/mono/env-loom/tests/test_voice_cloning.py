from __future__ import annotations

from loom_orchestrator.voice_cloning import (
    TIER_HD_S,
    TIER_LOW_S,
    TIER_MEDIUM_S,
    VoiceTier,
    compute_tier,
)

# ⚠ Seule la partie pure est testable ici — `ProgressiveVoiceCloner` appelle Pocket TTS/numpy.


def test_below_first_tier_keeps_fallback() -> None:
    assert compute_tier(TIER_LOW_S - 0.1) is VoiceTier.NONE


def test_tier_thresholds_are_inclusive() -> None:
    assert compute_tier(TIER_LOW_S) is VoiceTier.LOW
    assert compute_tier(TIER_MEDIUM_S) is VoiceTier.MEDIUM
    assert compute_tier(TIER_HD_S) is VoiceTier.HD


def test_tiers_are_monotonic() -> None:
    tiers = [compute_tier(s / 2) for s in range(0, int(TIER_HD_S * 2) + 10)]
    assert tiers == sorted(tiers)

