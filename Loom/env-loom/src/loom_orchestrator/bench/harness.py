from __future__ import annotations

import argparse
import asyncio
import time
from dataclasses import dataclass
from pathlib import Path

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (repo QuentinFuxa/WhisperLiveKit,
# commit lu le 2026-07-07) : `from whisperlivekit import AudioProcessor, TranscriptionEngine`
# est l'import public exact.
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    format_report,
    load_events,
)
from loom_orchestrator.bench.instrumentation import STAGE_WLK, EventLogger, LatencyEvent
from loom_orchestrator.bench.replay import replay_realtime
from loom_orchestrator.bench.timestamps import hms_to_seconds


@dataclass
class BenchmarkResult:
    log_path: Path
    transcript_path: Path


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
) -> BenchmarkResult:
    """T0.4 — une commande = replay du corpus + latences + transcript FR (pour lecture qualité).

    ⚠ Ne mesure aujourd'hui que l'étage WLK (audio → ligne commitée) : les étages
    orchestrateur/TTS n'existent pas encore (Phase 2 du backlog).

    ✓ Vérifié par lecture directe du code source (whisperlivekit/audio_processor.py,
    whisperlivekit/timed_objects.py, repo QuentinFuxa/WhisperLiveKit, lu le 2026-07-07) :
    - `AudioProcessor.process_audio(bytes)` alimente l'audio, `b""` signale la fin de flux.
    - `await AudioProcessor.create_tasks()` retourne un générateur async (`results_formatter`).
    - `response.to_dict()` = `{"status", "lines": [{"speaker": int, "text": str,
      "start": "H:MM:SS.cc", "end": "H:MM:SS.cc", "translation"?: str,
      "detected_language"?: str}], "buffer_transcription", "buffer_diarization",
      "buffer_translation", "remaining_time_transcription", "remaining_time_diarization", ...}`.
    - `lines` est **cumulatif** (mode `"full"`, le défaut d'`AudioProcessor` — renvoie tout
      l'historique à chaque poll, pas un delta) : le suivi de `known_line_count` ci-dessous est
      donc correct, pas une hypothèse à vérifier.
    - `start`/`end` sont formatés par `format_time()` en `H:MM:SS.cc` (précision au
      **centième de seconde**, pas à la seconde entière — la limite de précision notée dans une
      version précédente de ce module était fausse).
    - `pcm_input=True` est **obligatoire** dans la config du moteur : sans ce flag, WLK route
      l'audio entrant vers un process FFmpeg (pensé pour de l'audio compressé façon navigateur,
      webm/opus) au lieu de le traiter comme du PCM brut 16kHz mono — notre replay casserait
      silencieusement sans ce flag.
    """
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    run_id = f"{corpus_key}-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    out_dir.mkdir(parents=True, exist_ok=True)

    # ✓ Champs vérifiés contre whisperlivekit/config.py (WhisperLiveKitConfig) : pcm_input,
    # diarization, lan, target_language, diarization_backend existent bien avec ces noms.
    # ⚠ `TranscriptionEngine` est un singleton process-wide (whisperlivekit/core.py) : rejouer
    # ce benchmark avec une config différente (ex. grille T1.2) sans redémarrer le process exige
    # `TranscriptionEngine.reset()` d'abord, sinon la config précédente reste active.
    engine = TranscriptionEngine(
        pcm_input=True,
        diarization=True,
        diarization_backend="sortformer",
        lan="auto",
        target_language="fr",
    )
    processor = AudioProcessor(transcription_engine=engine, mode="full")

    with EventLogger(log_path) as logger, transcript_path.open("w", encoding="utf-8") as transcript:
        replay_start_monotonic = time.monotonic()
        known_line_count = 0

        async def send(chunk_bytes: bytes) -> None:
            await processor.process_audio(chunk_bytes)

        async def consume() -> None:
            nonlocal known_line_count
            results_generator = await processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])

                for idx, line in enumerate(lines[known_line_count:], start=known_line_count):
                    start = line.get("start")
                    text = line.get("translation") or line.get("text")
                    if start is None or not text:
                        # Marqueur de silence (speaker == -2, text=None) ou ligne sans texte
                        # exploitable — ignoré du benchmark, ne pollue pas les stats de latence.
                        continue

                    segment_id = f"{corpus_key}-line{idx}"
                    t_in = hms_to_seconds(start)
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                    speaker = line.get("speaker", "?")
                    transcript.write(f"[{speaker}] {text}\n")
                    transcript.flush()

                known_line_count = len(lines)

        consumer_task = asyncio.create_task(consume())
        await replay_realtime(wav_path, send)
        await processor.process_audio(b"")  # signale la fin du flux (cf. API WLK)
        # Laisse le temps aux derniers résultats WLK d'arriver après la fin de l'audio.
        await asyncio.sleep(2.0)
        consumer_task.cancel()

    return BenchmarkResult(log_path=log_path, transcript_path=transcript_path)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Benchmark Loom (T0.4) : replay corpus + latences + transcript FR."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    args = parser.parse_args()

    result = asyncio.run(run_benchmark(args.corpus_key, args.out_dir, args.corpus_dir))

    events = load_events(result.log_path)
    reports = aggregate_by_stage(events)
    end_to_end = aggregate_end_to_end(events)
    if end_to_end is not None:
        reports.append(end_to_end)

    print(format_report(reports))
    print(f"\nLog : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")


if __name__ == "__main__":
    main()
