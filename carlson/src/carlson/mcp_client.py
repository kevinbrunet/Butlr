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

Lifetime : la connexion est maintenue dans une tâche asyncio de fond (_task).
anyio impose que le cancel scope d'un task group soit entré et sorti depuis
la même tâche — on ne peut donc pas faire __aenter__/__aexit__ manuels sur
streamablehttp_client. Le pattern correct est un background task qui garde
le `async with` ouvert et attend un stop_event.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from typing import Any

import httpx
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client
from mcp.shared._httpx_utils import MCP_DEFAULT_SSE_READ_TIMEOUT, MCP_DEFAULT_TIMEOUT

log = logging.getLogger("carlson.mcp_client")

_MAX_RETRIES = 5
_INITIAL_BACKOFF_S = 1.0


def _unverified_http_client_factory(
    headers: dict[str, str] | None = None,
    timeout: httpx.Timeout | None = None,
    auth: httpx.Auth | None = None,
) -> httpx.AsyncClient:
    """Factory httpx sans vérification TLS — pour le certificat dev auto-signé de mcp-home."""
    return httpx.AsyncClient(
        headers=headers or {},
        timeout=timeout or httpx.Timeout(MCP_DEFAULT_TIMEOUT, read=MCP_DEFAULT_SSE_READ_TIMEOUT),
        auth=auth,
        follow_redirects=True,
        verify=False,  # certificat auto-signé en dev (cf. ADR 0003 — à remplacer par un vrai cert en prod)
    )


@dataclass
class ToolSchema:
    name: str
    description: str
    parameters: dict[str, Any]  # JSON Schema object (type/properties/required)


class McpHomeClient:
    """Async wrapper around the mcp Streamable HTTP client.

    Lifecycle : ``await client.start()`` au démarrage, ``await client.stop()``
    dans le bloc ``finally`` de ``main.py``.

    La connexion est maintenue dans une tâche de fond. ``start()`` retourne
    dès que la session est établie (ou que toutes les tentatives ont échoué).
    ``stop()`` signale la tâche et attend qu'elle se termine.
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
        self._task: asyncio.Task[None] | None = None
        self._stop_event = asyncio.Event()
        self._ready_event = asyncio.Event()

    async def _run(self) -> None:
        """Background task : connect, stay alive, disconnect on stop."""
        headers: dict[str, str] | None = (
            {"Authorization": f"Bearer {self._token}"} if self._token else None
        )
        backoff = _INITIAL_BACKOFF_S

        for attempt in range(1, _MAX_RETRIES + 1):
            try:
                async with streamablehttp_client(
                    self._url,
                    headers=headers,
                    httpx_client_factory=_unverified_http_client_factory,
                ) as (
                    read,
                    write,
                    _,
                ):
                    async with ClientSession(read, write) as session:
                        await session.initialize()
                        result = await session.list_tools()
                        self._tools = [
                            ToolSchema(
                                name=t.name,
                                description=t.description or "",
                                parameters=t.inputSchema,
                            )
                            for t in result.tools
                        ]
                        log.info(
                            "MCP tools disponibles : %s", [t.name for t in self._tools]
                        )
                        self._session = session
                        self._ready_event.set()
                        await self._stop_event.wait()
                        return
            except Exception as exc:
                log.warning(
                    "MCP client démarrage échoué (tentative %d/%d) : %s",
                    attempt,
                    _MAX_RETRIES,
                    exc,
                    exc_info=True,
                )
                if attempt < _MAX_RETRIES:
                    await asyncio.sleep(backoff)
                    backoff = min(backoff * 2, 30.0)

        log.error(
            "MCP client ne peut pas joindre mcp-home après %d tentatives — "
            "tool calling désactivé pour cette session.",
            _MAX_RETRIES,
        )
        self._ready_event.set()  # débloque start() même en cas d'échec total

    async def start(self) -> None:
        """Spawn the background connection task and wait until ready (or failed)."""
        log.info("Starting MCP client → %s", self._url)
        if not self._token:
            log.warning(
                "MCP_HOME_TOKEN is empty — running without auth. OK only on a "
                "trusted LAN (cf. ADR 0003)."
            )
        self._stop_event.clear()
        self._ready_event.clear()
        self._task = asyncio.create_task(self._run(), name="mcp-home-client")
        await self._ready_event.wait()

    async def stop(self) -> None:
        """Signal the background task to exit and wait for it."""
        self._stop_event.set()
        if self._task is not None:
            try:
                await self._task
            except Exception:
                pass
            self._task = None
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
