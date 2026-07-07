from __future__ import annotations

import argparse
import asyncio
import time
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

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
from loom_orchestrator.translation_seamless import SeamlessTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer


@dataclass
class PipelineBenchmarkResult:
    log_path: Path
    transcript_path: Path
    audio_dir: Path


def _write_wav(path: Path, audio: "np.ndarray", sample_rate_hz: int) -> None:
    # ⚠ Format de sortie de TTSModel.generate_audio() non confirmé par exécution réelle
    # (cf. tts_pocket.PocketTtsSynthesizer) — on suppose du float dans [-1, 1] comme la
    # plupart des TTS, et on clippe avant conversion PCM16. À corriger si le premier run
    # réel montre un tenseur déjà en int16 (le clip/scale serait alors silencieusement faux).
    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    diarization: bool = True,
    lan: str = "auto",
    target_lang: str = "fr",
) -> PipelineBenchmarkResult:
    """Premier câblage bout-en-bout (préliminaire à T2.3) : WLK (STT+diarisation) → segment
    audio source par tour de parole → SeamlessM4T v2 (traduction) → Pocket TTS (synthèse
    FR, voix de repli unique, pas de clonage par locuteur — cf. `tts_pocket.py`). Pas encore
    l'orchestrateur final : pas de file bornée, pas de registre de voix par locuteur,
    traitement strictement séquentiel d'un tour à la fois (cf. `main.py`, toujours
    `NotImplementedError` pour le vrai T2.3).

    ⚠ Constaté empiriquement (premier run réel, corpus `a`, 2026-07-15) et corrigé depuis :
    la première version scellait un tour dès qu'un nouvel index apparaissait dans `lines` —
    faux sur ce corpus, où `lines` reste à 2 entrées pendant tout le fichier (un narrateur
    continu + un second index qui clignote sans jamais devenir un 3e index) : le scellement
    prématuré a coupé le premier tour à ~6s et perdu toute la croissance ultérieure de
    `lines[0]` (jamais retraitée), ne laissant que 2 extraits de quelques mots. Politique
    corrigée : chaque ligne de `lines` (un segment WLK = un tour, cf. schéma vérifié dans
    `harness.py`) n'est traduite/synthétisée **qu'une fois le flux terminé**, avec son texte
    final. Pas d'incrémental par tour dans ce harnais (contrairement à `STAGE_WLK`, mesuré en
    continu) — cf. `main.py`/T2.3 pour une vraie politique de commit en flux.

    ⚠ Conséquence attendue sur un narrateur continu (corpus `a`) : un seul tour couvrant
    quasiment tout le fichier (~185s, ~2000 mots) part en une seule fois vers Seamless puis
    Pocket TTS — pas encore de découpage des tours trop longs (hors scope de ce premier
    câblage). Sur `b` (2 locuteurs qui alternent), `lines` contient plusieurs entrées de
    taille raisonnable (cf. `bench-runs/b-*.transcript.txt` du run WLK seul), donc plus
    représentatif pour juger la qualité FR.

    ⚠ Le traitement d'un tour de parole (Seamless + TTS, tous deux déportés en executor via
    `asyncio.to_thread`) est awaited séquentiellement dans la tâche qui consomme aussi les
    résultats WLK : un tour lent ralentit la lecture du flux de résultats WLK (jamais son
    traitement interne, cf. "TTS en retard = dégradation contrôlée, jamais de blocage amont"),
    et fait grandir la file interne de résultats déjà bufferisée par WLK. Pas de plafond
    explicite ici — c'est le travail de l'orchestrateur final (T2.3), pas de ce harnais de
    validation.
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
    translator = SeamlessTranslator()
    synth = PocketTtsSynthesizer()

    with EventLogger(log_path) as logger:
        replay_start_monotonic = time.monotonic()
        known_texts: list[str] = []
        last_lines: list[dict] = []

        async def send(chunk_bytes: bytes) -> None:
            await processor.process_audio(chunk_bytes)

        async def process_turn(idx: int, line: dict) -> None:
            text = line.get("text")
            start, end = line.get("start"), line.get("end")
            if not text or start is None or end is None:
                return

            start_s, end_s = hms_to_seconds(start), hms_to_seconds(end)
            speaker = line.get("speaker", "?")
            segment_id = f"{corpus_key}-turn{idx}"
            source_audio = read_segment(wav_path, start_s, end_s)

            fr_text = await asyncio.to_thread(translator.translate, source_audio, target_lang)
            t_translate_end = time.monotonic() - replay_start_monotonic
            logger.log(LatencyEvent.create(segment_id, STAGE_SEAMLESS, end_s, t_translate_end))

            audio_fr = await asyncio.to_thread(synth.synthesize, fr_text)
            t_tts_end = time.monotonic() - replay_start_monotonic
            logger.log(LatencyEvent.create(segment_id, STAGE_TTS, t_translate_end, t_tts_end))

            _write_wav(audio_dir / f"turn{idx}.wav", audio_fr, synth.sample_rate_hz)
            with transcript_path.open("a", encoding="utf-8") as f:
                f.write(f"[{speaker}] source : {text}\n[{speaker}] FR : {fr_text}\n\n")

        async def consume() -> None:
            results_generator = await processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])
                last_lines[:] = lines

                for idx, line, text in extract_updates(lines, known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    segment_id = f"{corpus_key}-line{idx}-{len(text)}"
                    t_in = hms_to_seconds(end)
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

        consumer_task = asyncio.create_task(consume())
        await replay_realtime(wav_path, send)
        await processor.process_audio(b"")  # signale la fin du flux (cf. API WLK)
        await asyncio.sleep(2.0)
        consumer_task.cancel()

        for idx, line in enumerate(last_lines):
            await process_turn(idx, line)

    return PipelineBenchmarkResult(
        log_path=log_path, transcript_path=transcript_path, audio_dir=audio_dir
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Premier câblage bout-en-bout Loom : WLK (STT+diarisation) → "
        "SeamlessM4T v2 (traduction) → Pocket TTS (synthèse FR), par tour de parole."
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
    print(f"Audio FR par tour : {result.audio_dir}")


if __name__ == "__main__":
    main()
