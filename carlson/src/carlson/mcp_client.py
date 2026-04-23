"""MCP client bridge — exposes MCP tools as OpenAI-style function schemas.

Transport : **SSE/HTTP** (cf. ADR 0003). mcp-home est un service long-running
accessible à `MCP_HOME_URL`. Carlson ouvre une connexion SSE au démarrage,
s'authentifie par bearer token (`MCP_HOME_TOKEN`) et liste les tools.

Plus de spawn en sous-processus — ce module ne touche jamais stdin/stdout.

Flux :
  1. Ouvrir la session SSE vers `MCP_HOME_URL` avec l'entête Authorization.
  2. `list_tools()` → stocker les schemas MCP.
  3. `tools_as_openai()` les convertit en function schemas OpenAI-compatibles
     (consommés par le LLM servi par llama.cpp server, cf. ADR 0006).
  4. Sur tool_call émis par le LLM, `call(name, args)` le forwarde à mcp-home
     et renvoie un résultat texte.

Stub volontaire pour la Phase 0 — l'implémentation réelle arrive à la Phase 3
(wiring SDK `mcp` Python avec le client SSE). ~ L'API exacte du SDK (noms de
context managers, shape de Session) dépend de la version pinnée : à valider
au moment du wiring.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Any

# Imports lazy au moment du wiring réel :
# from mcp.client.sse import sse_client
# from mcp.client.session import ClientSession

log = logging.getLogger("carlson.mcp_client")


@dataclass
class ToolSchema:
    name: str
    description: str
    parameters: dict[str, Any]  # JSON Schema


def tool_to_openai(schema: ToolSchema) -> dict[str, Any]:
    """Convert an MCP tool schema into the OpenAI functions format."""
    return {
        "type": "function",
        "function": {
            "name": schema.name,
            "description": schema.description,
            "parameters": schema.parameters,
        },
    }


class McpHomeClient:
    """Thin async wrapper around the mcp SSE client. Stub — Phase 3."""

    def __init__(self, url: str, token: str = "") -> None:
        self._url = url
        self._token = token
        self._tools: list[ToolSchema] = []

    async def start(self) -> None:
        log.info("Starting MCP client → %s", self._url)
        if not self._token:
            log.warning(
                "MCP_HOME_TOKEN is empty — running without auth. OK only on a "
                "trusted LAN (cf. ADR 0003)."
            )

    async def stop(self) -> None:
        pass

    def tools_as_openai(self) -> list[dict[str, Any]]:
        return [tool_to_openai(t) for t in self._tools]

    async def call(self, tool_name: str, arguments: dict[str, Any]) -> str:
        log.info("Calling MCP tool %s with args %s", tool_name, arguments)
        return f"(stub) called {tool_name}"
