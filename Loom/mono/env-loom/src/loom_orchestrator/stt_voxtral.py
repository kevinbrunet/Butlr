from __future__ import annotations

import asyncio
import base64
import json
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import AsyncIterator

# Variante STT Voxtral (ADR-0049) : client du serveur vLLM qui sert
# `mistralai/Voxtral-Mini-4B-Realtime-2602` sur `/v1/realtime` (websocket), derrière la même
# interface que `whisperlivekit.AudioProcessor` telle que l'orchestrateur l'utilise —
# `process_audio(bytes)` (PCM16 16 kHz mono, `b""` = fin de flux) et `create_tasks()` (flux
# de réponses dont `.to_dict()["lines"]` liste des lignes `{"text", "end", "speaker"}`).
# L'orchestrateur ne change donc que la fabrique de processeur (`--stt {wlk,voxtral}`).
#
# ✓ Protocole lu dans l'exemple officiel vLLM `examples/speech_to_text/realtime` (doc stable,
#   lue le 2026-10-26) : `session.created` à la connexion, puis `session.update` (modèle),
#   `input_audio_buffer.commit` (prêt), `input_audio_buffer.append` (audio base64 PCM16
#   16 kHz), `input_audio_buffer.commit` + `final: true` (fin) ; en retour
#   `transcription.delta` (`delta`), `transcription.done` (`text`), `error`.
# ⚠ Voxtral ne renvoie ni horodatage ni découpage en lignes : `end` est *estimé* ici (audio
#   envoyé au moment du delta, moins le délai de transcription configuré côté serveur), et
#   les lignes sont des phrases (ponctuation forte) ou des segments scellés après une pause.
#   Ni l'un ni l'autre n'est encore validé sur corpus réel.

SAMPLE_RATE_HZ = 16_000
BYTES_PER_SAMPLE = 2
DEFAULT_URL = "ws://127.0.0.1:8100/v1/realtime"
DEFAULT_MODEL = "mistralai/Voxtral-Mini-4B-Realtime-2602"
# ✓ Délai recommandé par la fiche du modèle (480 ms, réglé côté serveur dans `tekken.json`,
#   `transcription_delay_ms`) — ne sert ici qu'à estimer `end`, il ne règle pas le serveur.
DEFAULT_DELAY_S = 0.48
# ⚠ Non calibré : audio sans nouveau texte au-delà duquel la ligne active est scellée (rôle
#   d'une fin de tour WLK — déclenche le commit final de ce qui reste sans ponctuation).
DEFAULT_PAUSE_SEAL_S = 1.5
SENTENCE_END_CHARS = frozenset(".!?…")
# ✓ Constaté le 2026-10-26 (corpus a, sonde isolée, vLLM 0.32.0) : au bout de ~20 s de session,
#   Voxtral Realtime cesse de ponctuer et de mettre des majuscules (« …thought alice without
#   pictures or conversation so she was… »). Sans ponctuation, aucune phrase n'était scellée
#   et `commit_policy.compute_flush` ne trouvait aucun point de coupe : rien n'était traduit
#   pendant plus d'une minute. On scelle donc la ligne active à une frontière de mot dès
#   qu'elle dépasse ce nombre de mots. ⚠ Valeur non calibrée (compromis contexte de
#   traduction / latence). 15 au départ ; 30 depuis le ré-ancrage ci-dessous, qui rétablit la
#   ponctuation : à 15, la coupe tombait encore souvent au milieu des phrases.
DEFAULT_MAX_LINE_WORDS = 30
# ✓ Constaté le 2026-10-26 (sonde, corpus a, 185 s) : la perte de ponctuation est durable dans
#   une session (7 ponctuations sur 639 mots) mais une nouvelle session la rétablit : avec une
#   session neuve toutes les 20 s, 37 ponctuations et 56 majuscules au lieu de 7 et 13. Ni
#   `--tokenizer-mode mistral` ni un délai de 960 ms n'y changent rien. Hypothèse cohérente
#   avec vllm#47614 : le modèle relit ses propres sorties, une dérive s'auto-entretient. On
#   rouvre donc la session (« ré-ancrage ») au bout de ROTATE_S, de préférence pendant une
#   pause (pas de texte depuis ROTATE_PAUSE_S) pour ne pas couper un mot, au plus tard à
#   ROTATE_MAX_S. ⚠ Valeurs non calibrées.
DEFAULT_ROTATE_S = 20.0
DEFAULT_ROTATE_MAX_S = 30.0
DEFAULT_ROTATE_PAUSE_S = 0.6


def should_rotate(
    session_audio_s: float,
    since_text_s: float,
    rotate_s: float = DEFAULT_ROTATE_S,
    rotate_max_s: float = DEFAULT_ROTATE_MAX_S,
    pause_s: float = DEFAULT_ROTATE_PAUSE_S,
) -> bool:
    """Faut-il rouvrir la session Voxtral ? Après `rotate_s` d'audio si l'on est dans une
    pause (aucun texte depuis `pause_s`), et dans tous les cas après `rotate_max_s`. Pur."""
    if session_audio_s >= rotate_max_s:
        return True
    return session_audio_s >= rotate_s and since_text_s >= pause_s


def seconds_to_hms(seconds: float) -> str:
    """Format "H:MM:SS.cc" de WLK (cf. `bench/timestamps.hms_to_seconds`)."""
    seconds = max(0.0, seconds)
    hours = int(seconds // 3600)
    minutes = int(seconds % 3600 // 60)
    return f"{hours}:{minutes:02d}:{seconds % 60:05.2f}"


@dataclass
class SentenceLines:
    """Découpe le texte append-only de Voxtral en lignes au sens de WLK : une ligne par
    phrase, scellée dès sa ponctuation forte suivie d'un blanc, ou par `seal_active` après
    une pause. Une ligne scellée ne change plus ; seule la dernière (active) grandit — le
    contrat dont dépendent `extract_updates` et `commit_policy.compute_flush`. Pur."""

    speaker: str = "1"
    max_words: int = DEFAULT_MAX_LINE_WORDS
    sealed: list[dict] = field(default_factory=list)
    active_text: str = ""
    active_end_s: float = 0.0

    def append(self, delta: str, end_s: float) -> None:
        text = self.active_text + delta
        start = 0
        for i in range(len(text) - 1):
            if text[i] in SENTENCE_END_CHARS and text[i + 1].isspace():
                sentence = text[start : i + 1].strip()
                if sentence:
                    self.sealed.append(self._line(sentence, end_s))
                start = i + 1
        self.active_text = text[start:].lstrip() if start else text
        self.active_end_s = end_s
        # Ligne trop longue sans ponctuation forte : scellée au dernier espace. Le mot qui suit
        # (peut-être incomplet, les fragments de Voxtral commencent en général par l'espace)
        # reste actif.
        if len(self.active_text.split()) > self.max_words:
            cut = self.active_text.rfind(" ")
            head = self.active_text[:cut].strip()
            if head:
                self.sealed.append(self._line(head, end_s))
                self.active_text = self.active_text[cut:].lstrip()

    def seal_active(self) -> bool:
        """Scelle la ligne active (pause) ; retourne False s'il n'y avait rien à sceller."""
        if not self.active_text.strip():
            return False
        self.sealed.append(self._line(self.active_text.strip(), self.active_end_s))
        self.active_text = ""
        return True

    def lines(self) -> list[dict]:
        out = list(self.sealed)
        if self.active_text.strip():
            out.append(self._line(self.active_text.strip(), self.active_end_s))
        return out

    def _line(self, text: str, end_s: float) -> dict:
        return {"text": text, "end": seconds_to_hms(end_s), "speaker": self.speaker}


@dataclass
class VoxtralResponse:
    lines: list[dict]

    def to_dict(self) -> dict:
        return {"lines": self.lines}


@dataclass
class _Session:
    ws: object
    started_at_audio_s: float
    done: asyncio.Event = field(default_factory=asyncio.Event)


class VoxtralRealtimeProcessor:
    """Flux audio vers Voxtral, en sessions websocket `/v1/realtime` successives (ré-ancrage,
    cf. `should_rotate`), vues de l'extérieur comme un seul `AudioProcessor` WLK. En multi :
    un processeur par identité suivie, comme un `AudioProcessor` WLK par identité
    (ADR-0044).

    À chaque rotation, l'ancienne session reçoit sa fin de flux (`final`) et termine son texte
    en cours ; les fragments de la nouvelle sont retenus jusqu'à ce qu'elle ait fini, pour que
    le texte reste dans l'ordre. ⚠ Un mot à cheval sur la coupe peut être perdu ou mal
    transcrit (la coupe est cherchée dans une pause, sans garantie).
    """

    def __init__(
        self,
        url: str = DEFAULT_URL,
        model: str = DEFAULT_MODEL,
        delay_s: float = DEFAULT_DELAY_S,
        pause_seal_s: float = DEFAULT_PAUSE_SEAL_S,
        speaker: str = "1",
        rotate: bool = True,
    ) -> None:
        self._url = url
        self._model = model
        self._delay_s = delay_s
        self._pause_seal_s = pause_seal_s
        self._rotate = rotate
        self._lines = SentenceLines(speaker=speaker)
        self._session: _Session | None = None
        self._ready = asyncio.Event()
        self._responses: asyncio.Queue = asyncio.Queue()
        self._samples_sent = 0
        self._last_text_at_s = 0.0
        self._closed = False
        self.rotations = 0

    @property
    def _audio_s(self) -> float:
        return self._samples_sent / SAMPLE_RATE_HZ

    async def create_tasks(self) -> "AsyncIterator[VoxtralResponse]":
        await self._open_session(previous=None)
        self._ready.set()
        return self._iter_responses()

    async def _open_session(self, previous: _Session | None) -> None:
        import websockets

        ws = await websockets.connect(self._url, max_size=None)
        created = json.loads(await ws.recv())
        if created.get("type") != "session.created":
            raise RuntimeError(f"Voxtral : réponse inattendue à la connexion : {created!r}")
        await ws.send(json.dumps({"type": "session.update", "model": self._model}))
        await ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
        session = _Session(ws=ws, started_at_audio_s=self._audio_s)
        self._session = session
        asyncio.get_running_loop().create_task(self._receive(session, previous))

    async def _iter_responses(self) -> "AsyncIterator[VoxtralResponse]":
        while True:
            item = await self._responses.get()
            if item is None:
                return
            yield item

    def _apply_delta(self, delta: str, audio_s: float) -> None:
        self._lines.append(delta, audio_s - self._delay_s)
        if delta.strip():
            self._last_text_at_s = audio_s
        self._emit()

    async def _receive(self, session: _Session, previous: _Session | None) -> None:
        held: list[tuple[str, float]] = []  # fragments retenus tant que `previous` n'a pas fini
        try:
            async for message in session.ws:
                data = json.loads(message)
                kind = data.get("type")
                if kind == "transcription.delta":
                    delta = data.get("delta", "")
                    if not delta:
                        continue
                    if previous is not None and not previous.done.is_set():
                        held.append((delta, self._audio_s))
                        continue
                    for item in held:
                        self._apply_delta(*item)
                    held.clear()
                    self._apply_delta(delta, self._audio_s)
                elif kind == "transcription.done":
                    break
                elif kind == "error":
                    print(f"WARNING Voxtral : erreur serveur {data.get('error')!r}")
                    break
        finally:
            if previous is not None:
                await previous.done.wait()
            for item in held:
                self._apply_delta(*item)
            session.done.set()
            await session.ws.close()
            if self._closed and session is self._session:
                self._lines.seal_active()
                self._emit()
                self._responses.put_nowait(None)

    def _emit(self) -> None:
        self._responses.put_nowait(VoxtralResponse(self._lines.lines()))

    async def process_audio(self, pcm16: bytes) -> None:
        await self._ready.wait()
        if self._closed:
            return
        session = self._session
        if not pcm16:
            self._closed = True
            await session.ws.send(json.dumps({"type": "input_audio_buffer.commit", "final": True}))
            return
        self._samples_sent += len(pcm16) // BYTES_PER_SAMPLE
        await session.ws.send(
            json.dumps(
                {"type": "input_audio_buffer.append", "audio": base64.b64encode(pcm16).decode()}
            )
        )
        since_text_s = self._audio_s - self._last_text_at_s
        if since_text_s > self._pause_seal_s + self._delay_s and self._lines.seal_active():
            self._emit()
        if self._rotate and should_rotate(self._audio_s - session.started_at_audio_s, since_text_s):
            await session.ws.send(json.dumps({"type": "input_audio_buffer.commit", "final": True}))
            await self._open_session(previous=session)
            self.rotations += 1
