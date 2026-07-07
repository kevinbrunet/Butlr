from __future__ import annotations

from loom_orchestrator.translation_seamless import resolve_language_code


def test_resolve_language_code_maps_iso_639_1() -> None:
    assert resolve_language_code("en") == "eng"
    assert resolve_language_code("zh") == "cmn"
    assert resolve_language_code("fr") == "fra"


def test_resolve_language_code_passes_through_seamless_codes() -> None:
    assert resolve_language_code("eng") == "eng"
    assert resolve_language_code("cmn") == "cmn"
