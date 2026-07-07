"""Filler sidecar — speaks a patience phrase when a tool call is slow.

Design (see docs/adr/0004-filler-sidecar-pattern.md):
  - Observes FunctionCallInProgressFrame / FunctionCallResultFrame events.
  - If a tool has been running more than `delay_ms` and no text has been spoken
    in that window, emits a TTSSpeakFrame with a randomly picked phrase from
    the catalogue, matched on tool category and language.
  - Anti-repetition: tracks the last N phrases played, avoids duplicates.

This is a skeleton — wire it as a Pipecat FrameProcessor once the pipeline
is in place. ~ exact FrameProcessor API to pin when we install pipecat.
"""

from __future__ import annotations

import asyncio
import random
from collections import deque
from dataclasses import dataclass, field

# Phrase catalogue — extend generously, boredom is the enemy.
FILLERS: dict[str, dict[str, list[str]]] = {
    "search": {
        "fr": ["Je cherche ça…", "Un instant, je regarde…", "Je consulte."],
        "en": ["Let me look that up…", "One moment…", "Checking."],
    },
    "control": {
        "fr": ["Je m'en occupe.", "C'est parti.", "Tout de suite."],
        "en": ["On it.", "Right away.", "Doing it now."],
    },
    "weather": {
        "fr": ["Je consulte la météo.", "Je regarde le temps."],
        "en": ["Checking the weather.", "Looking at the forecast."],
    },
    "media": {
        "fr": ["Je lance ça.", "Un instant pour la musique."],
        "en": ["Starting it up.", "One moment for the music."],
    },
    "_default": {
        "fr": ["Un instant…", "Laissez-moi vérifier."],
        "en": ["One moment…", "Let me check."],
    },
}

# Mapping tool name → category. Unknown tools fall back to "_default".
TOOL_CATEGORY: dict[str, str] = {
    "turn_on_light": "control",
    "turn_off_light": "control",
    "set_color": "control",
    "get_device_state": "search",
    "get_time": "_default",  # instant, rarely triggers the filler anyway
    "get_weather": "weather",
    "play_media": "media",
}


@dataclass
class FillerPicker:
    history_size: int = 5
    _recent: deque[str] = field(default_factory=lambda: deque(maxlen=5))

    def pick(self, tool_name: str, language: str) -> str:
        category = TOOL_CATEGORY.get(tool_name, "_default")
        pool = FILLERS.get(category, FILLERS["_default"]).get(
            language, FILLERS["_default"]["fr"]
        )
        # Filter out recently played phrases when possible.
        candidates = [p for p in pool if p not in self._recent] or pool
        choice = random.choice(candidates)
        self._recent.append(choice)
        return choice


class FillerSidecar:
    """Skeleton — plug into Pipecat as a FrameProcessor.

    Expected integration:
      - Subscribe to FunctionCallInProgressFrame → schedule task.
      - Subscribe to FunctionCallResultFrame     → cancel task.
      - On task fire → emit TTSSpeakFrame(filler phrase).
    """

    def __init__(self, delay_ms: int, language: str = "fr") -> None:
        self._delay = delay_ms / 1000.0
        self._language = language
        self._picker = FillerPicker()
        self._pending: dict[str, asyncio.Task] = {}

    def set_language(self, language: str) -> None:
        self._language = language

    async def on_tool_start(self, call_id: str, tool_name: str) -> None:
        self._pending[call_id] = asyncio.create_task(self._maybe_speak(call_id, tool_name))

    async def on_tool_end(self, call_id: str) -> None:
        task = self._pending.pop(call_id, None)
        if task is not None:
            task.cancel()

    async def _maybe_speak(self, call_id: str, tool_name: str) -> None:
        try:
            await asyncio.sleep(self._delay)
            phrase = self._picker.pick(tool_name, self._language)
            await self._emit_tts(phrase)
        except asyncio.CancelledError:
            pass

    async def _emit_tts(self, phrase: str) -> None:
        # TODO: emit a pipecat TTSSpeakFrame here once the pipeline is wired.
        # For now, a no-op hook — overridden in tests.
        _ = phrase
