"""MCP client bridge — exposes MCP tools as OpenAI-style function schemas.

Transport : **Streamable HTTP** (cf. ADR 0003). mcp-home est un service
long-running accessible à `MCP_HOME_URL`, câblé avec `.WithHttpTransport()`
côté .NET. Carlson utilise donc `streamablehttp_client` du SDK Python.

Flux :
  1. Ouvrir la session vers `MCP_HOME_URL` avec l'entête Authorization.
  2. `list_tools()` → stocker les schémas MCP.
  3. `tools_as_pipecat()` les convertit en `ToolsSchema` Pipecat
     (FunctionSchema par outil), consommé par le LLMContext.
  4. Sur tool_call émis par le LLM, `call(name, args)` le forwarde à
     mcp-home et renvoie un résultat texte pour réinjection dans le contexte.

Reconnexion : backoff exponentiel, max 5 tentatives (~). Si mcp-home est
indisponible au démarrage, Carlson démarre sans tools et log une erreur.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from typing import Any

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

log = logging.getLogger("carlson.mcp_client")

_MAX_RETRIES = 5
_INITIAL_BACKOFF_S = 1.0


@dataclass
class ToolSchema:
    name: str
    description: str
    parameters: dict[str, Any]  # JSON Schema object (type/properties/required)


class McpHomeClient:
    """Async wrapper around the mcp Streamable HTTP client.

    Lifecycle : ``await client.start()`` au démarrage, ``await client.stop()``
    dans le bloc ``finally`` de ``main.py``.
    """

    def __init__(self, url: str, token: str = "") -> None:
        """
        Args:
            url: URL du endpoint MCP de mcp-home (ex. ``http://localhost:5090/mcp``).
            token: bearer token partagé. Chaîne vide = pas d'auth (dev local
                uniquement, déconseillé hors LAN privé — cf. ADR 0003).
        """
        self._url = url
        self._token = token
        self._tools: list[ToolSchema] = []
        self._session: ClientSession | None = None
        self._http_cm: Any = None
        self._session_cm: Any = None

    async def start(self) -> None:
        """Open Streamable HTTP session, list tools, store schemas.

        Retries with exponential backoff (max _MAX_RETRIES attempts).
        Failure is non-fatal: Carlson starts without tool-calling capability.
        """
        log.info("Starting MCP client → %s", self._url)
        if not self._token:
            log.warning(
                "MCP_HOME_TOKEN is empty — running without auth. OK only on a "
                "trusted LAN (cf. ADR 0003)."
            )
        headers: dict[str, str] | None = (
            {"Authorization": f"Bearer {self._token}"} if self._token else None
        )

        backoff = _INITIAL_BACKOFF_S
        for attempt in range(1, _MAX_RETRIES + 1):
            try:
                self._http_cm = streamablehttp_client(self._url, headers=headers)
                read, write, _ = await self._http_cm.__aenter__()
                self._session_cm = ClientSession(read, write)
                self._session = await self._session_cm.__aenter__()
                await self._session.initialize()
                result = await self._session.list_tools()
                self._tools = [
                    ToolSchema(
                        name=t.name,
                        description=t.description or "",
                        parameters=t.inputSchema,
                    )
                    for t in result.tools
                ]
                log.info("MCP tools disponibles : %s", [t.name for t in self._tools])
                return
            except Exception as exc:
                log.warning(
                    "MCP client démarrage échoué (tentative %d/%d) : %s",
                    attempt,
                    _MAX_RETRIES,
                    exc,
                )
                self._session = None
                self._session_cm = None
                self._http_cm = None
                if attempt < _MAX_RETRIES:
                    await asyncio.sleep(backoff)
                    backoff = min(backoff * 2, 30.0)

        log.error(
            "MCP client ne peut pas joindre mcp-home après %d tentatives — "
            "tool calling désactivé pour cette session.",
            _MAX_RETRIES,
        )

    async def stop(self) -> None:
        """Close the HTTP session cleanly."""
        if self._session_cm is not None:
            try:
                await self._session_cm.__aexit__(None, None, None)
            except Exception:
                pass
            self._session_cm = None
        if self._http_cm is not None:
            try:
                await self._http_cm.__aexit__(None, None, None)
            except Exception:
                pass
            self._http_cm = None
        self._session = None

    def tools_as_openai(self) -> list[dict[str, Any]]:
        """Return tools in OpenAI function-calling format (for debugging / tests)."""
        return [
            {
                "type": "function",
                "function": {
                    "name": t.name,
                    "description": t.description,
                    "parameters": t.parameters,
                },
            }
            for t in self._tools
        ]

    def tools_as_pipecat(self) -> Any | None:
        """Return a ToolsSchema for Pipecat's LLMContext, or None if no tools."""
        if not self._tools:
            return None
        from pipecat.adapters.schemas.function_schema import FunctionSchema
        from pipecat.adapters.schemas.tools_schema import ToolsSchema

        schemas = [
            FunctionSchema(
                name=t.name,
                description=t.description,
                properties=t.parameters.get("properties", {}),
                required=t.parameters.get("required", []),
            )
            for t in self._tools
        ]
        return ToolsSchema(standard_tools=schemas)

    async def call(self, tool_name: str, arguments: dict[str, Any]) -> str:
        """Invoke an MCP tool and return a plain-text result for the LLM."""
        if self._session is None:
            log.error("MCP tool '%s' appelé mais client non connecté", tool_name)
            return f"(erreur) mcp-home non disponible, impossible d'appeler {tool_name}"
        log.info("MCP tool_call → %s  args=%s", tool_name, arguments)
        result = await self._session.call_tool(tool_name, arguments)
        if result.isError:
            log.error("MCP tool '%s' a retourné une erreur : %s", tool_name, result.content)
            return f"(erreur de {tool_name})"
        parts = [c.text for c in result.content if hasattr(c, "text")]
        return "\n".join(parts) if parts else "(ok)"
