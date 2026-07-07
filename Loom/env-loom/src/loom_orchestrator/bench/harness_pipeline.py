from __future__ import annotations

import argparse
import asyncio
import time
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (repo QuentinFuxa/WhisperLiveKit,
# commit lu le 2026-07-07) : `from whisperlivekit import AudioProcessor, TranscriptionEngine`
# est l'import public exact.
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.alignatt import compute_increment
from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    format_report,
    load_events,
)
from loom_orchestrator.bench.audio_chunks import read_segment
from loom_orchestrator.bench.instrumentation import (
    STAGE_SEAMLESS,
    STAGE_TTS,
    STAGE_WLK,
    EventLogger,
    LatencyEvent,
)
from loom_orchestrator.bench.line_tracking import extract_updates
from loom_orchestrator.bench.replay import replay_realtime
from loom_orchestrator.bench.timestamps import hms_to_seconds
from loom_orchestrator.translation_seamless import AlignAttSeamlessTranslator, SeamlessTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ⚠ Pas encore benchmarké (ADR-0041) : évite de ré-encoder l'intégralité de l'audio d'une
# ligne à chaque mise à jour WLK (plusieurs fois par seconde, cf. logs des runs précédents) —
# n'appelle AlignAtt à nouveau pour une ligne que si son audio a grandi d'au moins cette durée
# depuis le dernier appel pour cette même ligne.
MIN_NEW_AUDIO_S = 1.0


@dataclass
class PipelineBenchmarkResult:
    log_path: Path
    transcript_path: Path
    audio_dir: Path


@dataclass
class LineCommitState:
    """État de commit AlignAtt + continuation TTS pour une ligne WLK — cf. ADR-0041."""

    committed_fr: str = ""
    last_alignatt_end_s: float = -MIN_NEW_AUDIO_S
    chunk_count: int = 0
    voice_state: object | None = None
    audio_chunks: list = field(default_factory=list)


def _write_wav(path: Path, audio: "np.ndarray", sample_rate_hz: int) -> None:
    # ⚠ Format de sortie de TTSModel.generate_audio_stream() non confirmé par exécution
    # réelle (cf. tts_pocket.PocketTtsSynthesizer) — on suppose du float dans [-1, 1] comme
    # la plupart des TTS, et on clippe avant conversion PCM16. À corriger si le premier run
    # réel montre un tenseur déjà en int16 (le clip/scale serait alors silencieusement faux).
    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


def _consume_continuation(
    synth: PocketTtsSynthesizer, state: object, text: str
) -> tuple[list, float]:
    """Épuise `synthesize_continuation` en thread (bloquant/CPU, cf. règle transverse) et
    mesure le délai jusqu'au premier chunk (TTFC, la métrique de budget de ADR-0036) — pas
    le temps total de synthèse de l'increment.
    """
    t0 = time.monotonic()
    chunks = []
    ttfc_s: float | None = None
    for chunk in synth.synthesize_continuation(state, text):
        if ttfc_s is None:
            ttfc_s = time.monotonic() - t0
        chunks.append(chunk)
    if ttfc_s is None:
        ttfc_s = time.monotonic() - t0
    return chunks, ttfc_s


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    diarization: bool = True,
    lan: str = "auto",
    target_lang: str = "fr",
) -> PipelineBenchmarkResult:
    """Câblage bout-en-bout (préliminaire à T2.3) : WLK (STT+diarisation) → politique de
    commit AlignAtt (ADR-0041) → SeamlessM4T v2 (traduction incrémentale) → Pocket TTS
    (synthèse FR, voix de repli unique, pas de clonage par locuteur — cf. `tts_pocket.py`).
    Pas encore l'orchestrateur final : pas de file bornée, pas de registre de voix par
    locuteur (cf. `main.py`, toujours `NotImplementedError` pour le vrai T2.3).

    Politique de commit (ADR-0041, révisée le 2026-07-15 — remplace une première version
    "attend la fin du flux", elle-même remplaçant une version encore antérieure "scelle sur
    nouvel index WLK", jamais implémentée) : pendant qu'une ligne WLK grandit, l'audio
    disponible pour cette ligne est retraduit par `AlignAttSeamlessTranslator.
    translate_partial` (throttlé par `MIN_NEW_AUDIO_S`), qui ne retourne que le préfixe
    "sûr" au sens AlignAtt. Le nouveau préfixe est diffé contre le texte déjà commité
    (`alignatt.compute_increment`) — seul l'increment part vers Pocket TTS. Quand une ligne
    se termine (nouvel index WLK = changement de locuteur, ou fin du flux), le texte restant
    est commité de force via `SeamlessTranslator.translate` (traduction complète, l'audio ne
    grandira plus) — filet de sécurité, cf. "le passé est immuable" (`Loom/CLAUDE.md`).

    ⚠ Le traitement d'un increment (Seamless + TTS, déportés en executor via
    `asyncio.to_thread`) est awaited séquentiellement dans la tâche qui consomme aussi les
    résultats WLK : un increment lent ralentit la lecture du flux de résultats WLK (jamais
    son traitement interne, cf. "TTS en retard = dégradation contrôlée, jamais de blocage
    amont"), et fait grandir la file interne de résultats déjà bufferisée par WLK. Pas de
    plafond explicite ici — c'est le travail de l'orchestrateur final (T2.3).

    Le chunking côté Seamless (dicté par AlignAtt, cf. ci-dessus) et le chunking audio côté
    TTS sont découplés (2026-07-15, suite à une question de Kevin) : chaque ligne a son
    propre état vocal Pocket TTS (`PocketTtsSynthesizer.new_line_state`), réutilisé avec
    `copy_state=False` (`synthesize_continuation`) à travers tous ses increments successifs
    — l'audio s'enchaîne comme un seul énoncé continu, pas des extraits disjoints malgré des
    increments de texte séparés. Un seul fichier `line{idx}.wav` par ligne (pas un par
    increment), réécrit à chaque nouvel increment.
    """
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    run_id = f"{corpus_key}-pipeline-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    audio_dir = out_dir / f"{run_id}-audio"
    out_dir.mkdir(parents=True, exist_ok=True)
    audio_dir.mkdir(parents=True, exist_ok=True)
    transcript_path.write_text("", encoding="utf-8")

    engine = TranscriptionEngine(
        pcm_input=True,
        diarization=diarization,
        diarization_backend="sortformer",
        lan=lan,
    )
    processor = AudioProcessor(transcription_engine=engine, mode="full")
    alignatt_translator = AlignAttSeamlessTranslator()
    final_translator = SeamlessTranslator()
    synth = PocketTtsSynthesizer()

    with EventLogger(log_path) as logger:
        replay_start_monotonic = time.monotonic()
        known_texts: list[str] = []
        last_lines: list[dict] = []
        commit_state: dict[int, LineCommitState] = {}
        sealed_committed: set[int] = set()

        async def send(chunk_bytes: bytes) -> None:
            await processor.process_audio(chunk_bytes)

        async def emit_increment(
            idx: int, speaker: str, increment: str, event_stage_t_in: float
        ) -> None:
            if not increment:
                return
            state = commit_state[idx]
            if state.voice_state is None:
                state.voice_state = synth.new_line_state()

            new_chunks, ttfc_s = await asyncio.to_thread(
                _consume_continuation, synth, state.voice_state, increment
            )
            t_first_chunk = event_stage_t_in + ttfc_s
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TTS, event_stage_t_in, t_first_chunk))

            state.audio_chunks.extend(new_chunks)
            import numpy as np

            full_audio = np.concatenate(state.audio_chunks)
            _write_wav(audio_dir / f"line{idx}.wav", full_audio, synth.sample_rate_hz)

            with transcript_path.open("a", encoding="utf-8") as f:
                f.write(f"[{speaker}] FR (increment) : {increment}\n")
            state.chunk_count += 1

        async def try_alignatt_commit(idx: int, line: dict) -> None:
            start, end = line.get("start"), line.get("end")
            if start is None or end is None:
                return
            start_s, end_s = hms_to_seconds(start), hms_to_seconds(end)

            state = commit_state.setdefault(idx, LineCommitState())
            if end_s - state.last_alignatt_end_s < MIN_NEW_AUDIO_S:
                return
            state.last_alignatt_end_s = end_s

            source_audio = read_segment(wav_path, start_s, end_s)
            safe_text = await asyncio.to_thread(
                alignatt_translator.translate_partial, source_audio, target_lang
            )
            t_translate_end = time.monotonic() - replay_start_monotonic
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_SEAMLESS, end_s, t_translate_end))

            increment, is_consistent = compute_increment(state.committed_fr, safe_text)
            if not is_consistent:
                print(
                    f"WARNING: AlignAtt incohérent sur line{idx} — texte sûr précédent non "
                    f"préfixé par le nouveau (committed={state.committed_fr!r}, "
                    f"new_safe={safe_text!r}). Increment ignoré, texte déjà commité conservé."
                )
                return

            state.committed_fr = safe_text
            speaker = line.get("speaker", "?")
            await emit_increment(idx, speaker, increment, t_translate_end)

        async def force_final_commit(idx: int, line: dict) -> None:
            text = line.get("text")
            start, end = line.get("start"), line.get("end")
            if not text or start is None or end is None:
                return
            start_s, end_s = hms_to_seconds(start), hms_to_seconds(end)
            speaker = line.get("speaker", "?")

            source_audio = read_segment(wav_path, start_s, end_s)
            final_fr = await asyncio.to_thread(
                final_translator.translate, source_audio, target_lang
            )
            t_translate_end = time.monotonic() - replay_start_monotonic
            state = commit_state.setdefault(idx, LineCommitState())
            segment_id = f"{corpus_key}-line{idx}-final"
            logger.log(LatencyEvent.create(segment_id, STAGE_SEAMLESS, end_s, t_translate_end))

            increment, is_consistent = compute_increment(state.committed_fr, final_fr)
            if not is_consistent:
                print(
                    f"WARNING: traduction finale de line{idx} incohérente avec le texte déjà "
                    f"commité (committed={state.committed_fr!r}, final={final_fr!r}). "
                    "Texte déjà commité conservé, tail final ignoré."
                )
                return

            state.committed_fr = final_fr
            await emit_increment(idx, speaker, increment, t_translate_end)

        async def consume() -> None:
            results_generator = await processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])
                last_lines[:] = lines

                for idx, line, _text in extract_updates(lines, known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    segment_id = f"{corpus_key}-wlk-line{idx}-{len(_text)}"
                    t_in = hms_to_seconds(end)
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                if not lines:
                    continue

                # Scelle définitivement toute ligne qui n'est plus la dernière (changement
                # de locuteur détecté) et pas encore scellée — traduction complète, pas
                # partielle, l'audio de cette ligne ne grandira plus.
                for idx in range(len(lines) - 1):
                    if idx not in sealed_committed:
                        await force_final_commit(idx, lines[idx])
                        sealed_committed.add(idx)

                # Commit incrémental AlignAtt sur la ligne active (la dernière, encore en
                # croissance) — throttlé par MIN_NEW_AUDIO_S à l'intérieur de la fonction.
                active_idx = len(lines) - 1
                await try_alignatt_commit(active_idx, lines[active_idx])

        consumer_task = asyncio.create_task(consume())
        await replay_realtime(wav_path, send)
        await processor.process_audio(b"")  # signale la fin du flux (cf. API WLK)
        await asyncio.sleep(2.0)
        consumer_task.cancel()

        for idx, line in enumerate(last_lines):
            if idx not in sealed_committed:
                await force_final_commit(idx, line)

    return PipelineBenchmarkResult(
        log_path=log_path, transcript_path=transcript_path, audio_dir=audio_dir
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Câblage bout-en-bout Loom : WLK (STT+diarisation) → commit AlignAtt "
        "(ADR-0041) → SeamlessM4T v2 → Pocket TTS (synthèse FR), incrémental par ligne."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--no-diarization", action="store_true")
    parser.add_argument("--lan", default="auto")
    parser.add_argument("--target-lang", default="fr")
    args = parser.parse_args()

    result = asyncio.run(
        run_benchmark(
            args.corpus_key,
            args.out_dir,
            args.corpus_dir,
            diarization=not args.no_diarization,
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
    print(f"Audio FR par ligne (un seul fichier continu par ligne) : {result.audio_dir}")


if __name__ == "__main__":
    main()
