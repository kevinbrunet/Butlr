"""Push-to-talk gate — slice 2.1.

Hold [SPACE] to record; release to trigger STT transcription.
Falls back to Enter-toggle if pynput is not available.

The gate mimics the same UserStartedSpeakingFrame / UserStoppedSpeakingFrame
pair that SileroVADAnalyzer emits, so the STT service handles both modes
identically.
"""

from __future__ import annotations

import asyncio
import logging
import threading
from typing import Optional

from pipecat.frames.frames import (
    Frame,
    InputAudioRawFrame,
    StartFrame,
    UserStartedSpeakingFrame,
    UserStoppedSpeakingFrame,
)
from pipecat.processors.frame_processor import FrameDirection, FrameProcessor

log = logging.getLogger("carlson.ptt")


class PTTGate(FrameProcessor):
    """Gates InputAudioRawFrame behind the SPACE key.

    When the key goes down:  emits UserStartedSpeakingFrame, then passes audio.
    When the key goes up:    emits UserStoppedSpeakingFrame → triggers STT.
    All other frames pass through unchanged.
    """

    def __init__(self) -> None:
        super().__init__()
        self._speaking = False
        self._loop: Optional[asyncio.AbstractEventLoop] = None

    # ------------------------------------------------------------------
    # FrameProcessor interface
    # ------------------------------------------------------------------

    async def process_frame(self, frame: Frame, direction: FrameDirection) -> None:
        await super().process_frame(frame, direction)

        if isinstance(frame, StartFrame):
            self._loop = asyncio.get_event_loop()
            threading.Thread(target=self._start_listener, daemon=True, name="ptt-kbd").start()
            await self.push_frame(frame, direction)

        elif isinstance(frame, InputAudioRawFrame):
            # Only forward audio while the PTT key is held.
            if self._speaking:
                await self.push_frame(frame, direction)

        else:
            await self.push_frame(frame, direction)

    # ------------------------------------------------------------------
    # Keyboard listeners (run in daemon threads)
    # ------------------------------------------------------------------

    def _start_listener(self) -> None:
        try:
            from pynput import keyboard  # noqa: PLC0415

            def on_press(key: object) -> None:
                if key == keyboard.Key.space and not self._speaking:
                    self._speaking = True
                    asyncio.run_coroutine_threadsafe(self._on_start(), self._loop)

            def on_release(key: object) -> None:
                if key == keyboard.Key.space and self._speaking:
                    self._speaking = False
                    asyncio.run_coroutine_threadsafe(self._on_stop(), self._loop)

            log.info("PTT: hold [SPACE] to record.")
            with keyboard.Listener(on_press=on_press, on_release=on_release) as listener:
                listener.join()

        except ImportError:
            log.warning("pynput not installed — using Enter-toggle PTT (press Enter once to start, once to stop).")
            self._stdin_listener()

    def _stdin_listener(self) -> None:
        import sys

        log.info("PTT: press [Enter] to toggle recording.")
        while True:
            sys.stdin.readline()
            if not self._speaking:
                self._speaking = True
                asyncio.run_coroutine_threadsafe(self._on_start(), self._loop)
            else:
                self._speaking = False
                asyncio.run_coroutine_threadsafe(self._on_stop(), self._loop)

    # ------------------------------------------------------------------
    # Async helpers (called from the event loop via run_coroutine_threadsafe)
    # ------------------------------------------------------------------

    async def _on_start(self) -> None:
        log.info("[PTT] ● Recording…")
        await self.push_frame(UserStartedSpeakingFrame(), FrameDirection.DOWNSTREAM)

    async def _on_stop(self) -> None:
        log.info("[PTT] ■ Sending to STT…")
        await self.push_frame(UserStoppedSpeakingFrame(), FrameDirection.DOWNSTREAM)
