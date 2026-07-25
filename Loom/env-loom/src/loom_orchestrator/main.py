from __future__ import annotations

import argparse
import asyncio
import signal
import time
from contextlib import nullcontext
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (cf. bench/harness_pipeline.py).
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.audio_io import (
    DryRunWavSink,
    LiveDeviceSink,
    capture_live,
    list_devices,
)
from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.instrumentation import (
    STAGE_TRANSLATE_LLM,
    STAGE_TTS,
    STAGE_WLK,
    EventLogger,
    LatencyEvent,
)
from loom_orchestrator.bench.line_tracking import extract_updates
from loom_orchestrator.bench.replay import replay_realtime
from loom_orchestrator.bench.timestamps import hms_to_seconds
from loom_orchestrator.commit_policy import compute_flush, force_flush
from loom_orchestrator.commit_state import (
    LineCommitState,
    _consume_stream,
    _release_gpu_state,
)
from loom_orchestrator.speaker_separation import (
    PYANNOTE_CHUNK_SAMPLES,
    SAMPLE_RATE_HZ,
    PyannoteVoiceSeparator,
    SpeakerEmbedder,
    VoiceSeparator,
)
from loom_orchestrator.speaker_tracking import (
    assign_streams_open_set,
    cosine_similarity,
    find_best_speaker,
    streams_are_distinct,
    update_ema_embedding,
    update_running_embedding,
)
from loom_orchestrator.translation_llm import LlmTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# Orchestrateur réel (T2.3, ADR-0045) — adapte le flux de `bench/harness_pipeline_dual.py`
# (séparation PixIT/SepFormer, référentiel de locuteurs ouvert avec EMA, WLK par identité,
# LlmTranslator, Pocket TTS — tout validé cette session sur des rejeux de fichiers WAV) à une
# capture audio live et une sortie mixée en direct (`audio_mixer.AudioMixer`, "au fil de
# l'eau" — cf. ADR-0045). Dupliqué plutôt que factorisé avec le harnais, même raisonnement
# que la non-fusion déjà actée entre `harness_pipeline.py` et `harness_pipeline_dual.py`
# (ADR-0044) : cette architecture n'a jamais tourné en direct, prématuré de la figer dans une
# abstraction partagée avant de savoir si elle tient la route telle quelle.
#
# ⚠ Premier jet (ADR-0045), pas encore testé sur la machine cible.
#
# Hors scope (cf. ADR-0044 §"Extension cible", ADR-0045 §Consequences) : canal dédié
# Bluetooth pour la voix de Kevin — cette version gère N locuteurs sur un seul périphérique
# d'entrée partagé, le référentiel ouvert n'a de toute façon pas de nombre fixe. Toutes les
# identités partagent la même voix Pocket TTS de repli (`estelle`, T3.1-T3.3 pas commencés) —
# deux locuteurs qui se chevauchent sonneront comme la même voix qui se parle dessus, à ne
# pas confondre avec un bug de mixage.

SEPARATION_WINDOW_S = 6.0
ROUTE_EVERY_S = 1.0
MIN_SEPARATION_AUDIO_S = 2.0
ROUTE_QUEUE_MAXSIZE = 5
CONSUME_SHUTDOWN_TIMEOUT_S = 20.0


@dataclass
class IdentitySession:
    """État par identité de locuteur suivie (ADR-0044) — équivalent du dict `commit_state`
    de harness_pipeline.py, mais tenu par identité plutôt que par ligne WLK d'un flux
    unique."""

    processor: AudioProcessor
    commit_state: dict[int, LineCommitState] = field(default_factory=dict)
    known_texts: list[str] = field(default_factory=list)
    sealed: set[int] = field(default_factory=set)
    last_lines: list[dict] = field(default_factory=list)


def _pcm16_bytes(audio: "np.ndarray") -> bytes:
    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    return pcm16.tobytes()


class _NoopEventLogger:
    """Substitut de `EventLogger` quand `--debug-dump-dir` n'est pas fourni — pas
    d'accumulation de fichiers pour une session sans fin naturelle (ADR-0045)."""

    def log(self, event: LatencyEvent) -> None:
        pass


async def run_live(
    source: str,
    corpus_key: str | None,
    corpus_dir: Path,
    source_lang: str,
    target_lang: str,
    lan: str,
    separator_backend: str,
    input_device: "int | str | None",
    output_device: "int | str | None",
    dry_run_wav: Path | None,
    debug_dump_dir: Path | None,
    debug: bool,
) -> None:
    def _debug(msg: str) -> None:
        if debug:
            print(msg)

    session_id = f"live-{int(time.time())}"
    log_path = None
    transcript_path = None
    if debug_dump_dir is not None:
        debug_dump_dir.mkdir(parents=True, exist_ok=True)
        log_path = debug_dump_dir / f"{session_id}.jsonl"
        transcript_path = debug_dump_dir / f"{session_id}.transcript.txt"
        transcript_path.write_text("", encoding="utf-8")

    # cf. bench/harness_pipeline_dual.py — même raisonnement pour désactiver Sortformer
    # quand separator_backend="pyannote" (ADR-0044 §Révisions 2026-07-17).
    use_sortformer = separator_backend != "pyannote"
    engine_kwargs = {"pcm_input": True, "diarization": use_sortformer, "lan": lan}
    if use_sortformer:
        engine_kwargs["diarization_backend"] = "sortformer"
    engine = TranscriptionEngine(**engine_kwargs)

    sessions: list[IdentitySession] = []
    if separator_backend == "pyannote":
        separator = PyannoteVoiceSeparator()
        separation_window_s = PYANNOTE_CHUNK_SAMPLES / SAMPLE_RATE_HZ
    elif separator_backend == "sepformer":
        separator = VoiceSeparator()
        separation_window_s = SEPARATION_WINDOW_S
    else:
        raise ValueError(
            f"separator_backend inconnu : {separator_backend!r} — attendu 'sepformer' ou "
            "'pyannote'"
        )
    embedder = SpeakerEmbedder()
    llm_translator = LlmTranslator()
    synth = PocketTtsSynthesizer()

    if dry_run_wav is not None:
        sink = DryRunWavSink(dry_run_wav, synth.sample_rate_hz)
    else:
        sink = LiveDeviceSink(synth.sample_rate_hz, device=output_device)

    known_embeddings: list[list[float] | None] = []
    embedding_counts: list[int] = []
    # cf. bench/harness_pipeline_dual.py pour la raison d'être de identity_timeline/
    # _to_global_seconds (ADR-0044) : AudioProcessor.process_audio ne reçoit que des octets
    # PCM bruts, sans horodatage — chaque identité ne reçoit qu'une fraction discontinue de
    # l'audio, son horloge interne dérive donc de l'horloge murale réelle.
    identity_timeline: list[list[tuple[int, int, int]]] = []

    def _record_send(ident: int, n_samples: int, global_start_sample: int) -> None:
        timeline = identity_timeline[ident]
        processor_start = timeline[-1][1] if timeline else 0
        timeline.append((processor_start, processor_start + n_samples, global_start_sample))

    def _to_global_seconds(ident: int, processor_seconds: float) -> float:
        processor_sample = int(processor_seconds * SAMPLE_RATE_HZ)
        for processor_start, processor_end, global_start in identity_timeline[ident]:
            if processor_start <= processor_sample <= processor_end:
                return (global_start + (processor_sample - processor_start)) / SAMPLE_RATE_HZ
        return processor_seconds

    log_context = EventLogger(log_path) if log_path is not None else nullcontext(_NoopEventLogger())
    with log_context as logger:
        session_start_monotonic = time.monotonic()

        async def emit_increment(
            ident: int,
            idx: int,
            speaker: str,
            increment: str,
            event_stage_t_in: float,
            is_final: bool = False,
        ) -> None:
            if not increment:
                return
            state = sessions[ident].commit_state[idx]

            # ⚠ Pas de lock ici : LlmTranslator/PocketTtsSynthesizer sont partagés entre
            # toutes les sessions du référentiel ouvert, mais commit_worker (plus bas) est
            # l'unique tâche qui les appelle — la sérialisation vient de la structure (un
            # seul consommateur), pas d'un verrou explicite (cf. ADR-0044 §Révisions : un
            # premier jet appelait ceci depuis plusieurs tâches concurrentes et a provoqué un
            # crash CUDA dur dans llama.cpp — ne jamais paralléliser ces appels entre
            # identités, cf. docstring de `commit_worker`).
            new_chunks, ttfc_s = await asyncio.to_thread(_consume_stream, synth, increment)
            t_first_chunk = event_stage_t_in + ttfc_s
            segment_id = f"{session_id}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_TTS, event_stage_t_in, t_first_chunk, is_final=is_final
                )
            )

            for chunk in new_chunks:
                sink.push(ident, chunk)

            if transcript_path is not None:
                with transcript_path.open("a", encoding="utf-8") as f:
                    f.write(f"[id{ident}/{speaker}] FR (increment) : {increment}\n")
            state.chunk_count += 1

        async def try_llm_commit(ident: int, idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            if not text or end is None:
                return
            session = sessions[ident]
            state = session.commit_state.setdefault(idx, LineCommitState())
            segment, new_flushed, is_consistent = compute_flush(text, state.flushed_source)
            if not is_consistent:
                print(
                    f"WARNING id{ident}: WLK a révisé du texte déjà flushé sur line{idx} "
                    f"(source={text!r}, déjà flushé={state.flushed_source!r}). Ignoré."
                )
                return
            state.flushed_source = new_flushed
            if not segment:
                return

            end_s = _to_global_seconds(ident, hms_to_seconds(end))
            translated = await asyncio.to_thread(
                llm_translator.translate, segment, source_lang, target_lang
            )
            t_translate_end = time.monotonic() - session_start_monotonic
            segment_id = f"{session_id}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end))

            state.committed_fr = f"{state.committed_fr} {translated}".strip()
            speaker = line.get("speaker", "?")
            await emit_increment(ident, idx, speaker, translated, t_translate_end)

        async def force_final_commit_llm(ident: int, idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            session = sessions[ident]
            state = session.commit_state.setdefault(idx, LineCommitState())
            # `end` sert seulement à ancrer le log de latence — ne jamais en faire une
            # condition pour flush le texte lui-même : c'est le dernier appel jamais fait
            # pour cet idx, un `end` manquant (WLK n'a pas calculé de timestamp final sur
            # une coupure en plein milieu de phrase) ne doit pas faire disparaître du
            # contenu déjà transcrit sans aucun WARNING (bug trouvé par Kevin, tail final
            # "prevent me from deliberately" perdu silencieusement, cf. ADR-0044 §Révisions).
            if text:
                segment, new_flushed, is_consistent = force_flush(text, state.flushed_source)
                if not is_consistent:
                    print(
                        f"WARNING id{ident}: traduction finale de line{idx} incohérente "
                        f"(déjà flushé={state.flushed_source!r}, source={text!r}). Ignoré."
                    )
                else:
                    state.flushed_source = new_flushed
                    if segment:
                        translated = await asyncio.to_thread(
                            llm_translator.translate, segment, source_lang, target_lang
                        )
                        t_translate_end = time.monotonic() - session_start_monotonic
                        segment_id = f"{session_id}-id{ident}-line{idx}-chunk{state.chunk_count}"
                        if end is not None:
                            end_s = _to_global_seconds(ident, hms_to_seconds(end))
                            logger.log(
                                LatencyEvent.create(
                                    segment_id,
                                    STAGE_TRANSLATE_LLM,
                                    end_s,
                                    t_translate_end,
                                    is_final=True,
                                )
                            )
                        state.committed_fr = f"{state.committed_fr} {translated}".strip()
                        speaker = line.get("speaker", "?")
                        await emit_increment(
                            ident, idx, speaker, translated, t_translate_end, is_final=True
                        )
            _release_gpu_state(state)

        # Une seule tâche de fond (commit_worker) traite try_llm_commit/force_final_commit_llm
        # — jamais consume() directement (cf. docstring de commit_worker, ADR-0044).
        partial_queues: list[asyncio.Queue] = []
        consumer_tasks: list[asyncio.Task] = []
        final_queue: asyncio.Queue = asyncio.Queue()

        def _queue_latest(queue: asyncio.Queue, item: tuple) -> None:
            if queue.full():
                try:
                    queue.get_nowait()
                    queue.task_done()
                except asyncio.QueueEmpty:
                    pass
            queue.put_nowait(item)

        async def commit_worker() -> None:
            """Tâche de fond unique — sérialise `translate`/`synthesize_stream` entre
            toutes les identités par construction (un seul appelant). Ne jamais paralléliser
            ces appels entre identités : un premier jet du projet qui le faisait a provoqué
            un crash CUDA dur dans llama.cpp (`GGML_ASSERT(buffer) failed`, cf. ADR-0044
            §Révisions)."""
            while True:
                if not final_queue.empty():
                    ident, idx, line = await final_queue.get()
                    try:
                        await force_final_commit_llm(ident, idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit final
                        print(f"WARNING: force_final_commit_llm(id{ident}) a échoué ({exc!r}).")
                    finally:
                        final_queue.task_done()
                    continue

                did_work = False
                for ident, queue in enumerate(partial_queues):
                    if queue.empty():
                        continue
                    idx, line = queue.get_nowait()
                    queue.task_done()
                    did_work = True
                    try:
                        await try_llm_commit(ident, idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit partiel
                        print(f"WARNING: try_llm_commit(id{ident}) a échoué ({exc!r}).")
                if not did_work:
                    await asyncio.sleep(0.02)

        async def consume(ident: int) -> None:
            session = sessions[ident]
            results_generator = await session.processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])
                session.last_lines = lines

                for idx, line, _text in extract_updates(lines, session.known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    _debug(f"DEBUG wlk-text id{ident}/line{idx}: {_text!r}")
                    segment_id = f"{session_id}-id{ident}-wlk-line{idx}-{len(_text)}"
                    t_in = _to_global_seconds(ident, hms_to_seconds(end))
                    t_out = time.monotonic() - session_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                if not lines:
                    continue

                for idx in range(len(lines) - 1):
                    if idx not in session.sealed:
                        final_queue.put_nowait((ident, idx, lines[idx]))
                        session.sealed.add(idx)

                active_idx = len(lines) - 1
                _queue_latest(partial_queues[ident], (active_idx, lines[active_idx]))

        def _ensure_identity(ident: int) -> None:
            """Crée à la volée toute nouvelle identité jusqu'à `ident` inclus — référentiel
            de locuteurs ouvert, pas de nombre fixe (ADR-0044)."""
            while len(sessions) <= ident:
                new_ident = len(sessions)
                sessions.append(
                    IdentitySession(
                        processor=AudioProcessor(transcription_engine=engine, mode="full")
                    )
                )
                known_embeddings.append(None)
                embedding_counts.append(0)
                identity_timeline.append([])
                partial_queues.append(asyncio.Queue(maxsize=1))
                consumer_tasks.append(asyncio.create_task(consume(new_ident)))
                print(f"Nouveau locuteur détecté : id{new_ident}")

        async def route_window(
            window: "np.ndarray",
            increment_start: int,
            increment_len: int,
            global_start_sample: int,
        ) -> None:
            """Sépare `window` et route le nouvel incrément vers l'identité correspondante
            — cf. `bench/harness_pipeline_dual.py:route_window` pour la justification
            complète de chaque décision (référentiel ouvert, EMA pour les embeddings
            masqués, filtre par activité de diarisation native, tout validé cette session,
            ADR-0044 §Révisions)."""
            too_short_for_separation = len(window) / SAMPLE_RATE_HZ < MIN_SEPARATION_AUDIO_S

            streams = None
            native_overlap: bool | None = None
            active_slots: list[bool] | None = None
            if not too_short_for_separation:
                if separator_backend == "pyannote":
                    streams, native_overlap, active_slots = await asyncio.to_thread(
                        separator.separate_and_detect_overlap, window
                    )
                else:
                    streams = await asyncio.to_thread(separator.separate, window)

            stream_embeddings = None
            if not too_short_for_separation:
                stream_embeddings = list(
                    await asyncio.gather(*(asyncio.to_thread(embedder.embed, s) for s in streams))
                )

            is_distinct = (
                native_overlap
                if native_overlap is not None
                else (not too_short_for_separation and streams_are_distinct(stream_embeddings))
            )

            if not too_short_for_separation and is_distinct:
                if active_slots is not None:
                    active_indices = [i for i, active in enumerate(active_slots) if active]
                else:
                    active_indices = list(range(len(streams)))
                filtered_embeddings = [stream_embeddings[i] for i in active_indices]

                assignment = assign_streams_open_set(known_embeddings, filtered_embeddings)
                if assignment:
                    _ensure_identity(max(assignment))

                sends = []
                assignment_debug = []
                for local_idx, ident in enumerate(assignment):
                    stream_idx = active_indices[local_idx]
                    prior = known_embeddings[ident]
                    similarity = (
                        cosine_similarity(prior, stream_embeddings[stream_idx])
                        if prior is not None
                        else float("nan")
                    )
                    assignment_debug.append(f"id{ident}<-stream{stream_idx}(sim={similarity:.2f})")
                    known_embeddings[ident] = update_ema_embedding(
                        known_embeddings[ident], stream_embeddings[stream_idx]
                    )
                    embedding_counts[ident] += 1
                    increment_audio = streams[stream_idx][
                        increment_start : increment_start + increment_len
                    ]
                    _record_send(ident, len(increment_audio), global_start_sample)
                    sends.append(
                        sessions[ident].processor.process_audio(_pcm16_bytes(increment_audio))
                    )
                await asyncio.gather(*sends)
                _debug(f"DEBUG assignment: {' '.join(assignment_debug)}")
            else:
                mixture_embedding = await asyncio.to_thread(embedder.embed, window)
                active_ident, _similarity = find_best_speaker(known_embeddings, mixture_embedding)
                if active_ident is None:
                    active_ident = len(known_embeddings)
                _ensure_identity(active_ident)
                known_embeddings[active_ident] = update_running_embedding(
                    known_embeddings[active_ident],
                    mixture_embedding,
                    embedding_counts[active_ident],
                )
                embedding_counts[active_ident] += 1
                increment_audio = window[increment_start : increment_start + increment_len]
                _record_send(active_ident, len(increment_audio), global_start_sample)
                await sessions[active_ident].processor.process_audio(
                    _pcm16_bytes(increment_audio)
                )

        async def route_consumer(route_queue: "asyncio.Queue") -> None:
            while True:
                window, increment_start, increment_len, global_start_sample = (
                    await route_queue.get()
                )
                try:
                    await route_window(window, increment_start, increment_len, global_start_sample)
                except asyncio.CancelledError:
                    raise
                except Exception as exc:  # noqa: BLE001 — isole une erreur de séparation/routage
                    print(f"WARNING: route_window a échoué ({exc!r}) — incrément perdu.")
                finally:
                    route_queue.task_done()

        async def feed(route_queue: "asyncio.Queue") -> None:
            import numpy as np

            buffer = np.zeros(0, dtype=np.float32)
            routed_samples = 0
            window_samples = int(separation_window_s * SAMPLE_RATE_HZ)
            route_every_samples = int(ROUTE_EVERY_S * SAMPLE_RATE_HZ)
            drop_count = [0]

            async def send(chunk_bytes: bytes) -> None:
                nonlocal buffer, routed_samples

                pcm16 = np.frombuffer(chunk_bytes, dtype=np.int16)
                buffer = np.concatenate([buffer, pcm16.astype(np.float32) / 32768.0])

                unrouted = len(buffer) - routed_samples
                if unrouted < route_every_samples:
                    return

                window_start = max(0, len(buffer) - window_samples)
                window = buffer[window_start:].copy()
                increment_start = routed_samples - window_start
                increment_len = len(buffer) - routed_samples
                global_start_sample = routed_samples
                # `send` ne doit jamais attendre le traitement GPU (cf. Loom/CLAUDE.md,
                # "jamais de blocage amont") — politique de drop du plus ancien job en
                # attente si route_consumer est en retard.
                routed_samples = len(buffer)
                job = (window, increment_start, increment_len, global_start_sample)
                try:
                    route_queue.put_nowait(job)
                except asyncio.QueueFull:
                    drop_count[0] += 1
                    try:
                        route_queue.get_nowait()
                        route_queue.task_done()
                    except asyncio.QueueEmpty:
                        pass
                    route_queue.put_nowait(job)

            if source == "corpus":
                assert corpus_key is not None
                wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)
                await replay_realtime(wav_path, send)

                unrouted = len(buffer) - routed_samples
                if unrouted > 0:
                    window_start = max(0, len(buffer) - window_samples)
                    window = buffer[window_start:].copy()
                    increment_start = routed_samples - window_start
                    job = (window, increment_start, unrouted, routed_samples)
                    try:
                        route_queue.put_nowait(job)
                    except asyncio.QueueFull:
                        drop_count[0] += 1
                        try:
                            route_queue.get_nowait()
                            route_queue.task_done()
                        except asyncio.QueueEmpty:
                            pass
                        route_queue.put_nowait(job)

                await route_queue.join()
                print(f"Fin du corpus — jobs droppés (file pleine) = {drop_count[0]}")
                for identity_session in sessions:
                    await identity_session.processor.process_audio(b"")
            else:
                # Ne se termine jamais — annulée par le gestionnaire SIGINT (cf. run_live
                # plus bas). Pas de "fin de fichier" en direct.
                await capture_live(send, device=input_device)

        route_queue: asyncio.Queue = asyncio.Queue(maxsize=ROUTE_QUEUE_MAXSIZE)
        route_consumer_task = asyncio.create_task(route_consumer(route_queue))
        commit_worker_task = asyncio.create_task(commit_worker())
        feed_task = asyncio.create_task(feed(route_queue))

        loop = asyncio.get_running_loop()

        def _request_stop() -> None:
            print(
                "\nArrêt demandé (Ctrl-C) — fin en cours, laisse le temps aux dernières "
                "traductions..."
            )
            feed_task.cancel()

        if source == "mic":
            loop.add_signal_handler(signal.SIGINT, _request_stop)

        try:
            await feed_task
        except asyncio.CancelledError:
            pass

        # Attend que chaque consume(ident) se termine de lui-même plutôt qu'un délai fixe
        # suivi d'une annulation inconditionnelle (cf. ADR-0044 §Révisions — un délai fixe
        # s'est révélé insuffisant sous charge, perdant la toute fin d'une transcription).
        if consumer_tasks:
            if source == "mic":
                # WLK ne reçoit son flush b"" qu'une fois capture_live annulée — envoyé
                # explicitement ici, `feed()` ne l'a pas fait (contrairement au chemin
                # `corpus`, qui le fait naturellement à la fin du fichier).
                for identity_session in sessions:
                    await identity_session.processor.process_audio(b"")
            _done, pending = await asyncio.wait(
                consumer_tasks, timeout=CONSUME_SHUTDOWN_TIMEOUT_S
            )
            if pending:
                print(
                    f"WARNING: {len(pending)} session(s) consume() pas terminées après "
                    f"{CONSUME_SHUTDOWN_TIMEOUT_S}s — annulées, fin de contenu potentiellement "
                    "perdue."
                )
                for task in pending:
                    task.cancel()

        await final_queue.join()
        for queue in partial_queues:
            await queue.join()

        route_consumer_task.cancel()
        commit_worker_task.cancel()
        for task in consumer_tasks:
            task.cancel()

        for ident, identity_session in enumerate(sessions):
            for idx, line in enumerate(identity_session.last_lines):
                # `sealed` ne doit jamais conditionner ce dernier appel : `lines` n'est pas
                # append-only (cf. Loom/CLAUDE.md, ADR-0039) — un idx peut être scellé par
                # erreur si WLK a transitoirement eu plus de lignes avant de fusionner/
                # rewinder (`[SimulStreaming guard] ... resetting current segment`), ce qui
                # bloquait alors ce idx pour toujours (bug trouvé par Kevin, tail final
                # d'id3 jamais commis malgré du contenu réel restant). `force_final_commit_llm`
                # est idempotent via `force_flush` (segment vide si rien de neuf) — l'appeler
                # sans condition est donc toujours sûr.
                await force_final_commit_llm(ident, idx, line)

    sink.close()
    if log_path is not None:
        print(f"\nLog : {log_path}")
    if transcript_path is not None:
        print(f"Transcript : {transcript_path}")
    if dry_run_wav is not None:
        print(f"Audio mixé (dry-run) : {dry_run_wav}")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Orchestrateur live Loom (T2.3, ADR-0045) : capture audio (micro ou "
        "rejeu de corpus), séparation multi-locuteurs, traduction, TTS, mixage de sortie "
        "synchronisé au fil de l'eau."
    )
    parser.add_argument(
        "--source",
        choices=["mic", "corpus"],
        default="mic",
        help="Entrée live (micro) ou rejeu d'un fichier de corpus de test (défaut : mic).",
    )
    parser.add_argument(
        "--corpus-key",
        choices=[c.key for c in corpus.CORPUS_MANIFEST],
        help="Requis si --source corpus.",
    )
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument(
        "--source-lang",
        default="en",
        help="Langue source pour la traduction (défaut : en) — sans effet sur --lan (WLK).",
    )
    parser.add_argument("--target-lang", default="fr")
    parser.add_argument("--lan", default="auto", help="Langue/autodétection passée à WLK.")
    parser.add_argument(
        "--separator-backend",
        choices=["sepformer", "pyannote"],
        default="sepformer",
        help="Modèle de séparation de voix (ADR-0044) — 'pyannote' nécessite HF_TOKEN.",
    )
    parser.add_argument(
        "--list-devices", action="store_true", help="Liste les périphériques audio et quitte."
    )
    parser.add_argument(
        "--input-device", default=None, help="Index ou nom (sous-chaîne) du micro."
    )
    parser.add_argument(
        "--output-device", default=None, help="Index ou nom (sous-chaîne) de la sortie."
    )
    parser.add_argument(
        "--dry-run-wav",
        type=Path,
        default=None,
        help="Écrit la sortie mixée dans ce fichier WAV au lieu de jouer en direct (test sûr, "
        "cf. ADR-0045 §Vérification).",
    )
    parser.add_argument(
        "--debug-dump-dir",
        type=Path,
        default=None,
        help="Écrit le log de latences JSONL + le transcript FR (off par défaut — pas "
        "d'accumulation de fichiers pour une session sans fin naturelle).",
    )
    parser.add_argument(
        "--debug", action="store_true", help="Affiche les traces de diagnostic détaillées."
    )
    args = parser.parse_args()

    if args.list_devices:
        print(list_devices())
        return

    if args.source == "corpus" and args.corpus_key is None:
        parser.error("--corpus-key est requis avec --source corpus")

    input_device: "int | str | None" = args.input_device
    if input_device is not None and input_device.isdigit():
        input_device = int(input_device)
    output_device: "int | str | None" = args.output_device
    if output_device is not None and output_device.isdigit():
        output_device = int(output_device)

    asyncio.run(
        run_live(
            source=args.source,
            corpus_key=args.corpus_key,
            corpus_dir=args.corpus_dir,
            source_lang=args.source_lang,
            target_lang=args.target_lang,
            lan=args.lan,
            separator_backend=args.separator_backend,
            input_device=input_device,
            output_device=output_device,
            dry_run_wav=args.dry_run_wav,
            debug_dump_dir=args.debug_dump_dir,
            debug=args.debug,
        )
    )


if __name__ == "__main__":
    main()
