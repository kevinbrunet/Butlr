from __future__ import annotations

from loom_orchestrator.voice_personalization import FALLBACK_VOICE_POOL, pool_voice_name_for

# ⚠ Seule la partie pure du module (assignation round-robin) est testable ici — le reste de
# `voice_personalization.py` appelle Pocket TTS/numpy, non installés hors machine cible (même
# limite que `speaker_separation.py`, cf. Loom/CLAUDE.md).


def test_pool_voice_name_for_first_identity_is_estelle() -> None:
    assert pool_voice_name_for(0) == "estelle"


def test_pool_voice_name_for_is_deterministic() -> None:
    assert pool_voice_name_for(3) == pool_voice_name_for(3)


def test_pool_voice_name_for_distinct_within_pool_size() -> None:
    names = [pool_voice_name_for(i) for i in range(len(FALLBACK_VOICE_POOL))]
    assert len(set(names)) == len(FALLBACK_VOICE_POOL)


def test_pool_voice_name_for_wraps_around() -> None:
    assert pool_voice_name_for(len(FALLBACK_VOICE_POOL)) == pool_voice_name_for(0)
