from __future__ import annotations

from loom_orchestrator.translation_llm import build_messages, strip_thinking


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


def test_build_messages_with_history_replays_exchanges_as_turns() -> None:
    history = [("Alice was", "Alice était"), ("very tired", "très fatiguée")]
    messages = build_messages("of sitting", "en", "fr", history)
    assert [m["role"] for m in messages] == [
        "system", "user", "assistant", "user", "assistant", "user"
    ]
    assert messages[1:3] == [
        {"role": "user", "content": "Alice was"},
        {"role": "assistant", "content": "Alice était"},
    ]
    assert messages[-1] == {"role": "user", "content": "of sitting"}


def test_build_messages_keeps_a_stable_prefix_as_history_grows() -> None:
    history = [("Alice was", "Alice était")]
    first = build_messages("very tired", "en", "fr", history)
    history.append(("very tired", "très fatiguée"))
    second = build_messages("of sitting", "en", "fr", history)
    assert second[: len(first)] == first


def test_strip_thinking_removes_empty_or_filled_reasoning_block() -> None:
    assert strip_thinking("<think>\n\n</think>\n\nBonjour.") == "Bonjour."
    assert strip_thinking("<think>raisonnement</think> pensa Alice,") == "pensa Alice,"
    assert strip_thinking("  Oh là là !  ") == "Oh là là !"
