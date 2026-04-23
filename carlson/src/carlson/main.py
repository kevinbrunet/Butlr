"""Carlson entry point.

Usage:
    carlson                 # default run
    python -m carlson       # equivalent
"""

from __future__ import annotations

import asyncio
import logging
import threading
from typing import Any

import sounddevice as sd

from .config import Config
from .mcp_client import McpHomeClient
from .pipeline import build_pipeline

log = logging.getLogger("carlson")


def _list_audio_devices() -> None:
    print("\n=== Devices audio ===")
    devices = sd.query_devices()
    default_in, default_out = sd.default.device
    for i, dev in enumerate(devices):
        markers = []
        if dev["max_input_channels"] > 0:
            markers.append("in")
        if dev["max_output_channels"] > 0:
            markers.append("out")
        indicator = "►" if i in (default_in, default_out) else " "
        print(f"  {indicator} [{i:2d}] {dev['name']}  ({', '.join(markers)})")
    print(f"\n  Défaut entrée : [{default_in}]  sortie : [{default_out}]")
    print("=====================\n")


def _start_ptt_thread(gate: Any, loop: asyncio.AbstractEventLoop) -> None:
    """Launch a daemon thread monitoring Enter key for push-to-talk toggle."""

    def _run() -> None:
        print(
            "Push-to-talk actif. Appuyez sur Entrée pour commencer/arrêter "
            "l'enregistrement. Ctrl+C pour quitter.\n"
        )
        recording = False
        while True:
            try:
                input()
            except EOFError:
                break
            recording = not recording
            if recording:
                print("  [● Enregistrement en cours...]")
                asyncio.run_coroutine_threadsafe(gate.open(), loop)
            else:
                print("  [■ Envoi à Whisper...]")
                asyncio.run_coroutine_threadsafe(gate.close(), loop)

    thread = threading.Thread(target=_run, daemon=True, name="ptt-controller")
    thread.start()


async def _run() -> None:
    config = Config.from_env()
    mcp = McpHomeClient(url=config.mcp_home_url, token=config.mcp_home_token)
    await mcp.start()
    try:
        _list_audio_devices()

        # ~ PipelineRunner / PipelineTask — import here to surface version errors early
        from pipecat.pipeline.runner import PipelineRunner
        from pipecat.pipeline.task import PipelineTask

        pipeline, gate = await build_pipeline(config, mcp)

        if config.use_vad:
            log.info("Mode VAD actif — parlez directement.")
            print("VAD actif — parlez directement. Ctrl+C pour quitter.\n")
        else:
            loop = asyncio.get_running_loop()
            _start_ptt_thread(gate, loop)

        log.info("Carlson pipeline prêt — boucle principale démarrée.")
        runner = PipelineRunner()
        task = PipelineTask(pipeline)
        await runner.run(task)
    finally:
        await mcp.stop()


def main() -> None:
    logging.basicConfig(
        level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s"
    )
    asyncio.run(_run())


if __name__ == "__main__":
    main()
