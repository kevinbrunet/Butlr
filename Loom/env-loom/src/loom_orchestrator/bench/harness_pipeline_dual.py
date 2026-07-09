from __future__ import annotations

import argparse
import asyncio
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (cf. harness_pipeline.py).
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    format_report,
    load_events,
)
from loom_orchestrator.bench.harness_pipeline import (
    LineCommitState,
    _consume_continuation,
    _release_gpu_state,
    _write_wav,
)
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
from loom_orchestrator.speaker_separation import SAMPLE_RATE_HZ, SpeakerEmbedder, VoiceSeparator
from loom_orchestrator.speaker_tracking import (
    assign_and_bootstrap,
    pick_active_identity,
    streams_are_distinct,
    update_running_embedding,
)
from loom_orchestrator.translation_llm import LlmTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# Câblage bout-en-bout avec séparation en amont de WLK (ADR-0044) : contrairement à
# harness_pipeline.py (`--translator llm`), où l'audio mélangé va directement à un seul WLK,
# ici l'audio brut est séparé en continu AVANT WLK et routé vers l'AudioProcessor du
# locuteur suivi correspondant — un seul TranscriptionEngine partagé (poids chargés une
# fois, cf. ADR-0044 §Context), N_IDENTITIES AudioProcessor indépendants. Chaque processor
# traite ensuite sa ligne exactement comme le chemin "llm" de harness_pipeline.py
# (commit_policy + LlmTranslator + PocketTtsSynthesizer) — cette partie est dupliquée ici
# plutôt que factorisée avec harness_pipeline.py pour l'instant (mesurer d'abord si cette
# architecture tient la route avant de fusionner le code commun, cf. "pas d'abstraction
# prématurée", Loom/CLAUDE.md hérite des règles racine).
#
# ⚠ Premier jet (ADR-0044), pas encore validé sur la machine cible. Couvre uniquement
# translator="llm" (ADR-0043) — la séparation en aval (ADR-0042) reste le mécanisme du
# chemin "seamless", inchangé, cf. harness_pipeline.py.

N_IDENTITIES = 2

# Mêmes valeurs que harness_pipeline.py/ADR-0042 pour la fenêtre de séparation (zone de
# confort mesurée de SepFormer-WHAMR). ROUTE_EVERY_S est nouveau ici : contrairement à
# ADR-0042 (séparation ponctuelle juste avant un commit de traduction), la séparation
# tourne en continu — ROUTE_EVERY_S throttle la fréquence des appels, pas leur fenêtre.
# ⚠ Pas calibré empiriquement : implique qu'aucun audio ne peut atteindre WLK avant que
# SEPARATION_WINDOW_S se soit accumulé (latence de démarrage de ligne non mesurée) — à
# surveiller en premier sur un vrai run (cf. ADR-0044 Conséquences, pas encore documentée
# avant ce premier jet de code).
SEPARATION_WINDOW_S = 6.0
ROUTE_EVERY_S = 1.0

# ⚠ Pas calibré empiriquement (cf. Révisions ADR-0044, corrigé après le premier run réel qui
# a montré un pacing temps réel désynchronisé faute de découplage feed()/route_consumer()) —
# taille de départ, quelques secondes de retard tolérées avant de perdre de l'audio.
ROUTE_QUEUE_MAXSIZE = 5


@dataclass
class PipelineDualBenchmarkResult:
    log_path: Path
    transcript_path: Path
    audio_dir: Path


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


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    lan: str = "auto",
    target_lang: str = "fr",
) -> PipelineDualBenchmarkResult:
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)
    source_lang = next(c for c in corpus.CORPUS_MANIFEST if c.key == corpus_key).language

    run_id = f"{corpus_key}-dual-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    audio_dir = out_dir / f"{run_id}-audio"
    out_dir.mkdir(parents=True, exist_ok=True)
    audio_dir.mkdir(parents=True, exist_ok=True)
    transcript_path.write_text("", encoding="utf-8")

    engine = TranscriptionEngine(
        pcm_input=True, diarization=True, diarization_backend="sortformer", lan=lan
    )
    sessions = [
        IdentitySession(processor=AudioProcessor(transcription_engine=engine, mode="full"))
        for _ in range(N_IDENTITIES)
    ]
    separator = VoiceSeparator()
    embedder = SpeakerEmbedder()
    llm_translator = LlmTranslator()
    synth = PocketTtsSynthesizer()

    known_embeddings: list[list[float] | None] = [None] * N_IDENTITIES
    embedding_counts: list[int] = [0] * N_IDENTITIES

    # ⚠ Ajoutés après un crash CUDA réel (GGML_ASSERT(buffer) failed dans llama.cpp, cf.
    # Révisions ADR-0044) : LlmTranslator/PocketTtsSynthesizer sont partagés entre les
    # N_IDENTITIES sessions, qui tournent maintenant vraiment en parallèle (route_consumer
    # découplé du pacing). Ni llama-cpp-python ni Pocket TTS ne sont garantis thread-safe pour
    # des appels d'inférence concurrents sur la même instance — un `Lock` par modèle partagé
    # sérialise ces appels sans sérialiser tout le reste (séparation/embedding restent
    # naturellement séquentiels, un seul `route_consumer`).
    translate_lock = asyncio.Lock()
    synth_lock = asyncio.Lock()

    with EventLogger(log_path) as logger:
        replay_start_monotonic = time.monotonic()

        async def emit_increment(
            ident: int, idx: int, speaker: str, increment: str, event_stage_t_in: float
        ) -> None:
            if not increment:
                return
            state = sessions[ident].commit_state[idx]
            if state.voice_state is None:
                state.voice_state = synth.new_line_state()

            async with synth_lock:
                new_chunks, ttfc_s = await asyncio.to_thread(
                    _consume_continuation, synth, state.voice_state, increment
                )
            t_first_chunk = event_stage_t_in + ttfc_s
            segment_id = f"{corpus_key}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TTS, event_stage_t_in, t_first_chunk))

            state.audio_chunks.extend(new_chunks)
            import numpy as np

            full_audio = np.concatenate(state.audio_chunks)
            _write_wav(audio_dir / f"id{ident}-line{idx}.wav", full_audio, synth.sample_rate_hz)

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

            end_s = hms_to_seconds(end)
            async with translate_lock:
                translated = await asyncio.to_thread(
                    llm_translator.translate, segment, source_lang, target_lang
                )
            t_translate_end = time.monotonic() - replay_start_monotonic
            segment_id = f"{corpus_key}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end))

            state.committed_fr = f"{state.committed_fr} {translated}".strip()
            speaker = line.get("speaker", "?")
            await emit_increment(ident, idx, speaker, translated, t_translate_end)

        async def force_final_commit_llm(ident: int, idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            session = sessions[ident]
            state = session.commit_state.setdefault(idx, LineCommitState())
            if text and end is not None:
                segment, new_flushed, is_consistent = force_flush(text, state.flushed_source)
                if not is_consistent:
                    print(
                        f"WARNING id{ident}: traduction finale de line{idx} incohérente "
                        f"(déjà flushé={state.flushed_source!r}, source={text!r}). Ignoré."
                    )
                else:
                    state.flushed_source = new_flushed
                    if segment:
                        end_s = hms_to_seconds(end)
                        async with translate_lock:
                            translated = await asyncio.to_thread(
                                llm_translator.translate, segment, source_lang, target_lang
                            )
                        t_translate_end = time.monotonic() - replay_start_monotonic
                        segment_id = f"{corpus_key}-id{ident}-line{idx}-final"
                        logger.log(
                            LatencyEvent.create(
                                segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end
                            )
                        )
                        state.committed_fr = f"{state.committed_fr} {translated}".strip()
                        speaker = line.get("speaker", "?")
                        await emit_increment(ident, idx, speaker, translated, t_translate_end)
            _release_gpu_state(state)

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
                    segment_id = f"{corpus_key}-id{ident}-wlk-line{idx}-{len(_text)}"
                    t_in = hms_to_seconds(end)
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                if not lines:
                    continue

                for idx in range(len(lines) - 1):
                    if idx not in session.sealed:
                        await force_final_commit_llm(ident, idx, lines[idx])
                        session.sealed.add(idx)

                active_idx = len(lines) - 1
                await try_llm_commit(ident, active_idx, lines[active_idx])

        async def route_window(
            window: "np.ndarray", increment_start: int, increment_len: int
        ) -> None:
            """Sépare `window` (les dernières SEPARATION_WINDOW_S secondes d'audio brut
            accumulé) et route seulement le nouvel incrément (`[increment_start:
            increment_start + increment_len]`, la partie jamais encore envoyée à un
            processor — cf. "le passé est immuable", Loom/CLAUDE.md) vers l'identité
            correspondante.

            ⚠ Appelée depuis `route_consumer` (tâche de fond), jamais depuis `send` — cf.
            Révisions ADR-0044 : appeler ce genre de traitement GPU directement dans `send`
            (attendu par `replay_realtime` avant le chunk suivant) désynchronise le pacing
            temps réel dès que ce traitement dépasse le débit d'arrivée de l'audio, ce que le
            premier run réel a confirmé (étage `wlk` p95=9s, largement hors budget).
            """
            streams = await asyncio.to_thread(separator.separate, window)
            stream_embeddings = list(
                await asyncio.gather(*(asyncio.to_thread(embedder.embed, s) for s in streams))
            )

            if streams_are_distinct(stream_embeddings):
                assignment = assign_and_bootstrap(known_embeddings, stream_embeddings)
                sends = []
                for ident in range(N_IDENTITIES):
                    stream_idx = assignment[ident]
                    known_embeddings[ident] = update_running_embedding(
                        known_embeddings[ident],
                        stream_embeddings[stream_idx],
                        embedding_counts[ident],
                    )
                    embedding_counts[ident] += 1
                    increment_audio = streams[stream_idx][
                        increment_start : increment_start + increment_len
                    ]
                    sends.append(
                        sessions[ident].processor.process_audio(_pcm16_bytes(increment_audio))
                    )
                await asyncio.gather(*sends)
            else:
                mixture_embedding = await asyncio.to_thread(embedder.embed, window)
                active_ident = pick_active_identity(known_embeddings, mixture_embedding)
                known_embeddings[active_ident] = update_running_embedding(
                    known_embeddings[active_ident],
                    mixture_embedding,
                    embedding_counts[active_ident],
                )
                embedding_counts[active_ident] += 1
                increment_audio = window[increment_start : increment_start + increment_len]
                await sessions[active_ident].processor.process_audio(
                    _pcm16_bytes(increment_audio)
                )

        async def route_consumer(route_queue: "asyncio.Queue") -> None:
            """Draine `route_queue` en séquence (préserve l'ordre, évite les races sur
            `known_embeddings`/`embedding_counts`), découplé du rythme temps réel de `feed`
            (cf. docstring de `route_window`)."""
            while True:
                window, increment_start, increment_len = await route_queue.get()
                try:
                    await route_window(window, increment_start, increment_len)
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
            window_samples = int(SEPARATION_WINDOW_S * SAMPLE_RATE_HZ)
            route_every_samples = int(ROUTE_EVERY_S * SAMPLE_RATE_HZ)

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
                # Marqué routé immédiatement (avant même que route_consumer n'ait traité le
                # job) : `send` ne doit jamais attendre le traitement GPU, cf. docstring de
                # route_window. Politique de drop si route_consumer est en retard : le plus
                # ancien job en attente saute (audio réellement perdu pour ce tour de parole,
                # pas rejoué plus tard — "le passé est immuable"), jamais `send` ne bloque
                # (cf. "jamais de blocage amont", Loom/CLAUDE.md).
                routed_samples = len(buffer)
                job = (window, increment_start, increment_len)
                try:
                    route_queue.put_nowait(job)
                except asyncio.QueueFull:
                    try:
                        route_queue.get_nowait()
                        route_queue.task_done()
                    except asyncio.QueueEmpty:
                        pass
                    route_queue.put_nowait(job)

            await replay_realtime(wav_path, send)

            # Reliquat plus court que ROUTE_EVERY_S en fin de flux — routé sans throttle,
            # même politique de drop que send() si la file est pleine.
            unrouted = len(buffer) - routed_samples
            if unrouted > 0:
                window_start = max(0, len(buffer) - window_samples)
                window = buffer[window_start:].copy()
                increment_start = routed_samples - window_start
                job = (window, increment_start, unrouted)
                try:
                    route_queue.put_nowait(job)
                except asyncio.QueueFull:
                    try:
                        route_queue.get_nowait()
                        route_queue.task_done()
                    except asyncio.QueueEmpty:
                        pass
                    route_queue.put_nowait(job)

            await route_queue.join()
            for session in sessions:
                await session.processor.process_audio(b"")

        # File bornée (politique de drop du plus ancien, cf. `send`) entre le rythme temps
        # réel de `feed` et le traitement GPU de `route_consumer` — ROUTE_QUEUE_MAXSIZE jobs
        # de ROUTE_EVERY_S chacun, donc quelques secondes de retard tolérées avant de perdre
        # de l'audio. ⚠ Taille pas calibrée empiriquement, valeur de départ.
        route_queue: asyncio.Queue = asyncio.Queue(maxsize=ROUTE_QUEUE_MAXSIZE)
        route_consumer_task = asyncio.create_task(route_consumer(route_queue))
        consumer_tasks = [asyncio.create_task(consume(ident)) for ident in range(N_IDENTITIES)]
        await feed(route_queue)
        await asyncio.sleep(2.0)
        route_consumer_task.cancel()
        for task in consumer_tasks:
            task.cancel()

        for ident, session in enumerate(sessions):
            for idx, line in enumerate(session.last_lines):
                if idx not in session.sealed:
                    await force_final_commit_llm(ident, idx, line)

    return PipelineDualBenchmarkResult(
        log_path=log_path, transcript_path=transcript_path, audio_dir=audio_dir
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Câblage bout-en-bout avec séparation en amont de WLK (ADR-0044) : "
        "audio brut séparé en continu, routé vers un AudioProcessor par identité suivie, "
        "traduction LlmTranslator (ADR-0043) + Pocket TTS. Premier jet, pas encore validé."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--lan", default="auto")
    parser.add_argument("--target-lang", default="fr")
    args = parser.parse_args()

    result = asyncio.run(
        run_benchmark(
            args.corpus_key,
            args.out_dir,
            args.corpus_dir,
            lan=args.lan,
            target_lang=args.target_lang,
        )
    )

    events = load_events(result.log_path)
    reports = aggregate_by_stage(events)
    end_to_end = aggregate_end_to_end(events)
    if end_to_end is not None:
        reports.append(end_to_end)

    print(format_report(reports))
    print(f"\nLog : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")
    print(f"Audio FR par identité/ligne : {result.audio_dir}")


if __name__ == "__main__":
    main()
