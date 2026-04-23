"""Carlson entry point.

Usage:
    carlson                 # default run  (PTT mode unless CARLSON_VAD=1)
    python -m carlson       # equivalent
"""

from __future__ import annotations

import asyncio
import logging

import sounddevice as sd

from .config import Config
from .mcp_client import McpHomeClient
from .pipeline import build_pipeline

log = logging.getLogger("carlson")


def _list_audio_devices() -> None:
    """Log available audio devices so the user can verify mic/speaker selection."""
    try:
        devices = sd.query_devices()
        default_in, default_out = sd.default.device
        log.info("=== Audio devices ===")
        for i, d in enumerate(devices):
            tags: list[str] = []
            if d["max_input_channels"] > 0:
                tags.append("IN *" if i == default_in else "IN")
            if d["max_output_channels"] > 0:
                tags.append("OUT *" if i == default_out else "OUT")
            if tags:
                log.info("  [%2d] %-40s  (%s)", i, d["name"], ", ".join(tags))
        log.info("  Set AUDIODEVICE env var (device index) to override defaults.")
    except Exception as exc:
        log.warning("Could not query audio devices: %s", exc)


async def _run() -> None:
    from pipecat.pipeline.runner import PipelineRunner

    config = Config.from_env()
    _list_audio_devices()

    mcp = McpHomeClient(url=config.mcp_home_url, token=config.mcp_home_token)
    await mcp.start()
    try:
        task = await build_pipeline(config, mcp)
        runner = PipelineRunner()
        log.info("Carlson ready. %s",
                 "VAD mode — speak naturally." if config.use_vad
                 else "PTT mode — hold [SPACE] to speak, release to send.")
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
