"""Tests for the filler picker.

The sidecar's async behavior is tested with a fake TTS emitter in an
integration test once the pipeline is wired — for now, only the picker.
"""

from __future__ import annotations

from carlson.filler import FILLERS, FillerPicker


def test_picker_returns_phrases_in_the_requested_language() -> None:
    picker = FillerPicker()
    for _ in range(20):
        phrase = picker.pick("turn_on_light", language="fr")
        assert phrase in FILLERS["control"]["fr"]


def test_picker_falls_back_to_default_on_unknown_tool() -> None:
    picker = FillerPicker()
    phrase = picker.pick("no_such_tool", language="en")
    assert phrase in FILLERS["_default"]["en"]


def test_picker_avoids_immediate_repetition_when_pool_allows() -> None:
    picker = FillerPicker(history_size=3)
    seen = [picker.pick("turn_on_light", language="fr") for _ in range(3)]
    # Pool of FR "control" has 3 phrases — after 3 picks they should all differ.
    assert len(set(seen)) == len(FILLERS["control"]["fr"])
