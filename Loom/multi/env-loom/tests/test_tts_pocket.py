from __future__ import annotations

from loom_orchestrator.tts_pocket import max_speech_samples

# ⚠ Seule la partie pure est testable ici — `PocketTtsSynthesizer` charge Pocket TTS/torch.


def test_tts_budget_grows_with_text_length() -> None:
    assert max_speech_samples("Oui,", 24_000) < max_speech_samples("Un lapin blanc passa.", 24_000)


def test_tts_budget_never_zero_for_empty_text() -> None:
    assert max_speech_samples("", 24_000) > 0
