"""Carlson entry point.

Usage:
    carlson                 # default run
    python -m carlson       # equivalent
"""

from __future__ import annotations

import asyncio
import logging

from .config import Config
from .mcp_client import McpHomeClient
from .pipeline import build_pipeline

log = logging.getLogger("carlson")


async def _run() -> None:
    config = Config.from_env()
    mcp = McpHomeClient(url=config.mcp_home_url, token=config.mcp_home_token)
    await mcp.start()
    try:
        pipeline = await build_pipeline(config, mcp)
        log.info("Carlson pipeline ready — entering run loop.")
        # TODO: await pipeline.run() once Pipecat is wired.
        _ = pipeline
    finally:
        await mcp.stop()


def main() -> None:
    logging.basicConfig(
        level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s"
    )
    asyncio.run(_run())


if __name__ == "__main__":
    main()
