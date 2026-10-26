from __future__ import annotations

from loom_orchestrator.translation_llm import build_messages


def test_build_messages_uses_full_language_names() -> None:
    messages = build_messages("Hello there", "en", "fr")
    assert messages[0]["role"] == "system"
    assert "English" in messages[0]["content"]
    assert "French" in messages[0]["content"]
    assert messages[1] == {"role": "user", "content": "Hello there"}


def test_build_messages_falls_back_to_raw_code_for_unknown_language() -> None:
    messages = build_messages("你好", "zh", "xx")
    assert "Chinese" in messages[0]["content"]
    assert "xx" in messages[0]["content"]
