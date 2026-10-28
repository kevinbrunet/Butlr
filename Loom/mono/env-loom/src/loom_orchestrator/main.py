from __future__ import annotations

import argparse
import asyncio
import faulthandler
import signal
import time
from contextlib import nullcontext
from pathlib import Path
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
from loom_orchestrator.catch_up import CatchUp
from loom_orchestrator.speaker_identity import InputAudioHistory, Speaker, SpeakerIdentifier
from loom_orchestrator.commit_policy import compute_flush, force_flush
from loom_orchestrator.commit_state import (
    LineCommitState,
    _consume_stream,
    _release_gpu_state,
)
from loom_orchestrator.stt_voxtral import DEFAULT_URL as VOXTRAL_DEFAULT_URL
from loom_orchestrator.stt_voxtral import VoxtralRealtimeProcessor
from loom_orchestrator.translation_llm import LlmTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer
from loom_orchestrator.voice_cloning import ProgressiveVoiceCloner

# Orchestrateur mono-locuteur (ADR-0048) : un seul intervenant, donc aucune des briques qui
# servaient à isoler une voix dans `multi/` — ni séparation (SepFormer/pyannote), ni empreinte
# vocale (ECAPA), ni suivi d'identité, ni diarisation Sortformer, ni routage par identité. Le
# flux audio va droit au STT ; le reste (commit partiel/final par ligne, traduction Qwen,
# Pocket TTS, mixage au fil de l'eau) reprend `multi/env-loom/src/loom_orchestrator/main.py`
# (ADR-0045), dupliqué plutôt que partagé (ADR-0048 : deux sous-projets autonomes).
#
# STT au choix (`--stt`) : WhisperLiveKit embarqué (défaut, ADR-0039) ou Voxtral Realtime servi
# par vLLM (ADR-0049), derrière la même interface `process_audio`/`create_tasks`.
#
# ⚠ Premier jet, pas encore exécuté sur la machine cible.

CONSUME_SHUTDOWN_TIMEOUT_S = 20.0
SPEAKER_ID = 0


class _NoopEventLogger:
    """Substitut de `EventLogger` quand `--debug-dump-dir` n'est pas fourni (cf. ADR-0045)."""

    def log(self, event: LatencyEvent) -> None:
        pass


def _new_processor(stt: str, lan: str, voxtral_url: str):
    if stt == "voxtral":
        return VoxtralRealtimeProcessor(url=voxtral_url)
    if stt != "wlk":
        raise ValueError(f"stt inconnu : {stt!r} — attendu 'wlk' ou 'voxtral'")
    # ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (cf. multi/bench/harness_pipeline.py).
    from whisperlivekit import AudioProcessor, TranscriptionEngine

    engine = TranscriptionEngine(pcm_input=True, diarization=False, lan=lan)
    return AudioProcessor(transcription_engine=engine, mode="full")


async def run_live(
    source: str,
    corpus_key: str | None,
    corpus_dir: Path,
    source_lang: str,
    target_lang: str,
    lan: str,
    stt: str,
    voxtral_url: str,
    tts_device: str,
    translation_context: bool,
    catch_up_enabled: bool,
    speakers: bool,
    clone_voice: bool,
    input_device: "int | str | None",
    output_device: "int | str | None",
    dry_run_wav: Path | None,
    debug_dump_dir: Path | None,
    debug: bool,
) -> None:
    import numpy as np

    def _debug(msg: str) -> None:
        if debug:
            print(msg)

    session_id = f"mono-{int(time.time())}"
    log_path = None
    transcript_path = None
    if debug_dump_dir is not None:
        debug_dump_dir.mkdir(parents=True, exist_ok=True)
        log_path = debug_dump_dir / f"{session_id}.jsonl"
        transcript_path = debug_dump_dir / f"{session_id}.transcript.txt"
        transcript_path.write_text("", encoding="utf-8")

    processor = _new_processor(stt, lan, voxtral_url)
    llm_translator = LlmTranslator(keep_context=translation_context)
    synth = PocketTtsSynthesizer(device=tts_device)
    voice = ProgressiveVoiceCloner(synth)
    # Plusieurs personnes qui parlent chacune leur tour (`--speakers`) : une identité par
    # empreinte vocale, une voix de synthèse par identité (cf. speaker_identity.py).
    identifier = SpeakerIdentifier() if speakers else None
    input_history = InputAudioHistory() if speakers else None

    if dry_run_wav is not None:
        sink = DryRunWavSink(dry_run_wav, synth.sample_rate_hz)
    else:
        sink = LiveDeviceSink(synth.sample_rate_hz, device=output_device)
    catch_up = CatchUp(synth.sample_rate_hz) if catch_up_enabled else None

    commit_state: dict[int, LineCommitState] = {}
    known_texts: list[str] = []
    sealed: set[int] = set()
    last_lines: list[dict] = []

    log_context = EventLogger(log_path) if log_path is not None else nullcontext(_NoopEventLogger())
    with log_context as logger:
        session_start_monotonic = time.monotonic()

        async def emit_increment(
            idx: int,
            increment: str,
            event_stage_t_in: float,
            is_final: bool = False,
            source: str = "",
            speaker: Speaker | None = None,
        ) -> None:
            if not increment:
                return
            state = commit_state[idx]
            voice_state = (
                synth.get_named_voice_state(speaker.voice)
                if speaker is not None
                else voice.get_voice_state()
            )
            new_chunks, ttfc_s = await asyncio.to_thread(
                _consume_stream, synth, increment, voice_state
            )
            segment_id = f"{session_id}-line{idx}-chunk{state.chunk_count}"
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_TTS, event_stage_t_in, event_stage_t_in + ttfc_s,
                    is_final=is_final,
                )
            )
            if catch_up is not None and new_chunks:
                sink.push(SPEAKER_ID, catch_up.process(new_chunks, sink.pending_s(SPEAKER_ID)))
            else:
                for chunk in new_chunks:
                    sink.push(SPEAKER_ID, chunk)
            if transcript_path is not None:
                tag = f"[id{speaker.ident}/{speaker.voice}] " if speaker is not None else ""
                with transcript_path.open("a", encoding="utf-8") as f:
                    if source:
                        f.write(f"{tag}SRC (segment) : {source}\n")
                    f.write(f"{tag}FR (increment) : {increment}\n")
            state.chunk_count += 1

        async def translate_and_emit(
            idx: int, state: LineCommitState, segment: str, end: str | None, is_final: bool
        ) -> None:
            speaker = None
            if identifier is not None:
                if end is not None:
                    speaker = await asyncio.to_thread(
                        identifier.identify_segment, input_history, hms_to_seconds(end)
                    )
                else:
                    speaker = identifier.registry.current
            translated = await asyncio.to_thread(
                llm_translator.translate,
                segment,
                source_lang,
                target_lang,
                speaker.ident if speaker is not None else 0,
            )
            t_translate_end = time.monotonic() - session_start_monotonic
            if end is not None:
                segment_id = f"{session_id}-line{idx}-chunk{state.chunk_count}"
                logger.log(
                    LatencyEvent.create(
                        segment_id, STAGE_TRANSLATE_LLM, hms_to_seconds(end), t_translate_end,
                        is_final=is_final,
                    )
                )
            state.committed_fr = f"{state.committed_fr} {translated}".strip()
            await emit_increment(
                idx, translated, t_translate_end, is_final=is_final, source=segment,
                speaker=speaker,
            )

        async def try_llm_commit(idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            if not text or end is None:
                return
            state = commit_state.setdefault(idx, LineCommitState())
            segment, new_flushed, is_consistent = compute_flush(text, state.flushed_source)
            if not is_consistent:
                print(
                    f"WARNING: le STT a révisé du texte déjà flushé sur line{idx} "
                    f"(source={text!r}, déjà flushé={state.flushed_source!r}). Ignoré."
                )
                return
            state.flushed_source = new_flushed
            if segment:
                await translate_and_emit(idx, state, segment, end, is_final=False)

        async def force_final_commit_llm(idx: int, line: dict) -> None:
            # `end` n'ancre que le log de latence, jamais une condition de flush (cf. multi,
            # ADR-0044 §Révisions : fin de transcript perdue silencieusement sinon).
            text, end = line.get("text"), line.get("end")
            state = commit_state.setdefault(idx, LineCommitState())
            if text:
                segment, new_flushed, is_consistent = force_flush(text, state.flushed_source)
                if not is_consistent:
                    print(
                        f"WARNING: traduction finale de line{idx} incohérente "
                        f"(déjà flushé={state.flushed_source!r}, source={text!r}). Ignoré."
                    )
                else:
                    state.flushed_source = new_flushed
                    if segment:
                        await translate_and_emit(idx, state, segment, end, is_final=True)
            _release_gpu_state(state)

        partial_queue: asyncio.Queue = asyncio.Queue(maxsize=1)
        final_queue: asyncio.Queue = asyncio.Queue()
        rebuild_requested = asyncio.Event()
        stop_requested = asyncio.Event()

        def _queue_latest(queue: asyncio.Queue, item: object) -> None:
            if queue.full():
                try:
                    queue.get_nowait()
                    queue.task_done()
                except asyncio.QueueEmpty:
                    pass
            queue.put_nowait(item)

        async def commit_worker() -> None:
            """Tâche de fond unique : sérialise traduction, synthèse et clonage (appels GPU
            jamais parallélisés, cf. multi ADR-0044/0046 §Révisions).

            S'arrête d'elle-même (`stop_requested`, files vides) au lieu d'être annulée :
            annuler une tâche qui attend `asyncio.to_thread` n'arrête pas le thread, et le
            commit final de fin de run appelait alors llama.cpp/Pocket TTS en parallèle de
            l'appel encore en cours (segfault, exit 139, premier run réel du 2026-10-26)."""
            while True:
                if stop_requested.is_set() and final_queue.empty() and partial_queue.empty():
                    return
                if not final_queue.empty():
                    idx, line = await final_queue.get()
                    try:
                        await force_final_commit_llm(idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit final
                        print(f"WARNING: force_final_commit_llm a échoué ({exc!r}).")
                    finally:
                        final_queue.task_done()
                    continue

                # Reconstruction de voix (au plus une par palier, trois par session) : avant les
                # commits partiels, sinon jamais servie tant que le STT produit du texte.
                if rebuild_requested.is_set():
                    rebuild_requested.clear()
                    try:
                        await asyncio.to_thread(voice.rebuild)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de clonage
                        print(f"WARNING: clonage de voix a échoué ({exc!r}).")
                    continue

                did_work = False
                if not partial_queue.empty():
                    idx, line = partial_queue.get_nowait()
                    partial_queue.task_done()
                    did_work = True
                    try:
                        await try_llm_commit(idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit partiel
                        print(f"WARNING: try_llm_commit a échoué ({exc!r}).")

                if not did_work:
                    await asyncio.sleep(0.02)

        async def consume() -> None:
            nonlocal last_lines
            results_generator = await processor.create_tasks()
            async for response in results_generator:
                lines = response.to_dict().get("lines", [])
                last_lines = lines

                for idx, line, _text in extract_updates(lines, known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    _debug(f"DEBUG stt-text line{idx}: {_text!r}")
                    segment_id = f"{session_id}-stt-line{idx}-{len(_text)}"
                    t_out = time.monotonic() - session_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, hms_to_seconds(end), t_out))

                if not lines:
                    continue
                for idx in range(len(lines) - 1):
                    if idx not in sealed:
                        final_queue.put_nowait((idx, lines[idx]))
                        sealed.add(idx)
                active_idx = len(lines) - 1
                _queue_latest(partial_queue, (active_idx, lines[active_idx]))

        async def send(chunk_bytes: bytes) -> None:
            # Jamais d'attente du GPU ici (cf. Loom/CLAUDE.md, « jamais de blocage amont ») :
            # le STT bufferise, et le clonage ne fait qu'accumuler ; la reconstruction de voix
            # est confiée au consommateur unique.
            await processor.process_audio(chunk_bytes)
            if clone_voice or input_history is not None:
                pcm = np.frombuffer(chunk_bytes, dtype=np.int16).astype(np.float32) / 32768.0
                if input_history is not None:
                    input_history.append(pcm)
                if clone_voice and voice.add_audio(pcm):
                    rebuild_requested.set()

        async def feed() -> None:
            if source == "corpus":
                assert corpus_key is not None
                await replay_realtime(corpus.resolve(corpus_key, corpus_dir=corpus_dir), send)
                await processor.process_audio(b"")
            else:
                # Ne se termine jamais — annulée par le gestionnaire SIGINT (cf. plus bas).
                await capture_live(send, device=input_device)

        consumer_task = asyncio.create_task(consume())
        commit_worker_task = asyncio.create_task(commit_worker())
        feed_task = asyncio.create_task(feed())

        def _request_stop() -> None:
            print("\nArrêt demandé (Ctrl-C) — fin en cours, laisse le temps aux dernières traductions...")
            feed_task.cancel()

        if source == "mic":
            asyncio.get_running_loop().add_signal_handler(signal.SIGINT, _request_stop)

        try:
            await feed_task
        except asyncio.CancelledError:
            pass
        if source == "mic":
            await processor.process_audio(b"")

        _done, pending = await asyncio.wait([consumer_task], timeout=CONSUME_SHUTDOWN_TIMEOUT_S)
        if pending:
            print(
                f"WARNING: consume() pas terminé après {CONSUME_SHUTDOWN_TIMEOUT_S}s — annulé, "
                "fin de contenu potentiellement perdue."
            )
            consumer_task.cancel()

        stop_requested.set()
        await commit_worker_task

        # Dernier commit sans condition sur `sealed` (idempotent via `force_flush`, cf. multi
        # main.py et ADR-0044 §Révisions), protégé comme les autres.
        for idx, line in enumerate(last_lines):
            try:
                await force_final_commit_llm(idx, line)
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 — isole une erreur de commit final
                print(f"WARNING: force_final_commit_llm a échoué en fin de run ({exc!r}).")

    sink.close()
    if catch_up is not None:
        print(catch_up.summary())
    if identifier is not None:
        print(identifier.summary())
    if log_path is not None:
        print(f"\nLog : {log_path}")
    if transcript_path is not None:
        print(f"Transcript : {transcript_path}")
    if dry_run_wav is not None:
        print(f"Audio (dry-run) : {dry_run_wav}")


def main() -> None:
    faulthandler.enable()  # pile Python en cas de segfault (appels GPU natifs)
    parser = argparse.ArgumentParser(
        description="Orchestrateur Loom mono-locuteur (ADR-0048) : capture audio (micro ou "
        "rejeu de corpus), STT, traduction, TTS à voix clonée progressivement, sortie au fil "
        "de l'eau."
    )
    parser.add_argument("--source", choices=["mic", "corpus"], default="mic")
    parser.add_argument(
        "--corpus-key",
        choices=[c.key for c in corpus.CORPUS_MANIFEST],
        help="Requis si --source corpus (un corpus mono-locuteur : a, c ou d).",
    )
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--source-lang", default="en")
    parser.add_argument("--target-lang", default="fr")
    parser.add_argument("--lan", default="auto", help="Langue/autodétection passée à WLK.")
    parser.add_argument(
        "--stt",
        choices=["wlk", "voxtral"],
        default="wlk",
        help="WhisperLiveKit embarqué (défaut) ou Voxtral Realtime servi par vLLM (ADR-0049, "
        "lancer d'abord Loom/voxtral-server/serve.sh).",
    )
    parser.add_argument("--voxtral-url", default=VOXTRAL_DEFAULT_URL)
    parser.add_argument(
        "--tts-device",
        choices=["cuda", "cpu"],
        default="cuda",
        help="Périphérique de Pocket TTS (ADR-0051) : GPU par défaut, CPU pour l'ancien "
        "comportement (ADR-0036).",
    )
    parser.add_argument(
        "--translation-context",
        action="store_true",
        help="Traduit chaque segment à la suite des précédents du même locuteur, en "
        "réutilisant le cache KV de llama.cpp (expérimental, fenêtre 32k, sans compactage).",
    )
    parser.add_argument(
        "--speakers",
        action="store_true",
        help="Plusieurs personnes qui parlent chacune leur tour (sans chevauchement) : "
        "reconnaissance par empreinte vocale, une voix de synthèse par personne.",
    )
    parser.add_argument(
        "--no-catch-up",
        action="store_true",
        help="Désactive le rattrapage du retard de sortie (coupe des silences puis "
        "accélération sans changement de hauteur, cf. catch_up.py).",
    )
    parser.add_argument(
        "--voice-clone",
        action="store_true",
        help="Active le clonage progressif de la voix du locuteur (ADR-0048). Désactivé par "
        "défaut depuis Pocket TTS 4.0.1 : ✓ constaté le 2026-10-26 qu'une voix clonée depuis "
        "l'anglais fait générer au modèle french_24l 2 à 3 fois trop d'audio (fin de séquence "
        "rarement émise), et le pipeline décroche. Voix par défaut : estelle.",
    )
    parser.add_argument("--list-devices", action="store_true")
    parser.add_argument("--input-device", default=None)
    parser.add_argument("--output-device", default=None)
    parser.add_argument(
        "--dry-run-wav",
        type=Path,
        default=None,
        help="Écrit la sortie dans ce WAV au lieu de la jouer (test sûr, cf. ADR-0045).",
    )
    parser.add_argument("--debug-dump-dir", type=Path, default=None)
    parser.add_argument("--debug", action="store_true")
    args = parser.parse_args()
    if args.speakers and args.voice_clone:
        parser.error("--speakers et --voice-clone sont exclusifs : une voix précalculée par "
                     "personne, pas de clonage progressif d'une voix unique.")

    if args.list_devices:
        print(list_devices())
        return
    if args.source == "corpus" and args.corpus_key is None:
        parser.error("--corpus-key est requis avec --source corpus")

    def _device(value: str | None) -> "int | str | None":
        return int(value) if value is not None and value.isdigit() else value

    asyncio.run(
        run_live(
            source=args.source,
            corpus_key=args.corpus_key,
            corpus_dir=args.corpus_dir,
            source_lang=args.source_lang,
            target_lang=args.target_lang,
            lan=args.lan,
            stt=args.stt,
            voxtral_url=args.voxtral_url,
            tts_device=args.tts_device,
            translation_context=args.translation_context,
            catch_up_enabled=not args.no_catch_up,
            speakers=args.speakers,
            clone_voice=args.voice_clone,
            input_device=_device(args.input_device),
            output_device=_device(args.output_device),
            dry_run_wav=args.dry_run_wav,
            debug_dump_dir=args.debug_dump_dir,
            debug=args.debug,
        )
    )


if __name__ == "__main__":
    main()
