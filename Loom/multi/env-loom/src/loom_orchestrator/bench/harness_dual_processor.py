from __future__ import annotations

import argparse
import asyncio
import time
from pathlib import Path

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (repo QuentinFuxa/WhisperLiveKit,
# commit lu le 2026-07-07, cf. harness_pipeline.py) : import public exact.
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.replay import replay_realtime

# Harnais isolé (ADR-0044) : vérifie les deux inconnues les plus risquées de la séparation en
# amont de WLK avant d'investir dans la réécriture complète de harness_pipeline.py —
# (1) un seul TranscriptionEngine partagé entre 2 AudioProcessor double-t-il vraiment pas la
#     VRAM (hypothèse tirée de la doc officielle WhisperLiveKit — "Create a new AudioProcessor
#     for each connection, passing the shared engine" — jamais exécutée sur cette machine) ;
# (2) 2 AudioProcessor alimentés concurremment (asyncio, dans le même process) sur cet engine
#     partagé fonctionnent-ils sans deadlock ni corruption croisée (non documenté).
#
# ⚠ Ne fait PAS encore de séparation de voix réelle (VoiceSeparator) : chaque processor reçoit
# ici un fichier corpus DIFFÉRENT (déjà mono-locuteur chacun), pour isoler la question
# VRAM/concurrence de la question séparation (déjà validée séparément, ADR-0042,
# harness_separation.py). Une fois cette étape validée, la séparation réelle + le suivi
# d'identité (`speaker_tracking.py`, ADR-0044) viendront router un seul flux mélangé vers les
# deux processors — pas ce harnais.


async def _run_processor(label: str, processor: AudioProcessor, wav_path: Path) -> list[str]:
    lines_seen: list[str] = []

    async def send(chunk_bytes: bytes) -> None:
        await processor.process_audio(chunk_bytes)

    async def consume() -> None:
        results_generator = await processor.create_tasks()
        async for response in results_generator:
            data = response.to_dict()
            for line in data.get("lines", []):
                text = line.get("text", "")
                if text and text not in lines_seen:
                    lines_seen.append(text)

    consumer_task = asyncio.create_task(consume())
    await replay_realtime(wav_path, send)
    await processor.process_audio(b"")
    await asyncio.sleep(2.0)
    consumer_task.cancel()

    last = lines_seen[-1] if lines_seen else "(aucune)"
    print(f"[{label}] {len(lines_seen)} lignes vues, dernière : {last}")
    return lines_seen


def _vram_snapshot() -> str:
    import torch

    if not torch.cuda.is_available():
        return "CUDA indisponible"
    allocated = torch.cuda.memory_allocated() / 1e9
    reserved = torch.cuda.memory_reserved() / 1e9
    return f"allocated={allocated:.2f}GB reserved={reserved:.2f}GB"


async def run_benchmark(corpus_key_a: str, corpus_key_b: str, corpus_dir: Path) -> None:
    print(f"VRAM avant chargement : {_vram_snapshot()}")

    engine = TranscriptionEngine(
        pcm_input=True, diarization=True, diarization_backend="sortformer", lan="auto"
    )
    print(f"VRAM après 1 TranscriptionEngine (poids modèle) : {_vram_snapshot()}")

    processor_a = AudioProcessor(transcription_engine=engine, mode="full")
    processor_b = AudioProcessor(transcription_engine=engine, mode="full")
    print(f"VRAM après 2 AudioProcessor (même engine partagé) : {_vram_snapshot()}")

    wav_a = corpus.resolve(corpus_key_a, corpus_dir=corpus_dir)
    wav_b = corpus.resolve(corpus_key_b, corpus_dir=corpus_dir)

    t0 = time.monotonic()
    results = await asyncio.gather(
        _run_processor("A", processor_a, wav_a),
        _run_processor("B", processor_b, wav_b),
    )
    elapsed_s = time.monotonic() - t0

    print(f"VRAM après traitement concurrent : {_vram_snapshot()}")
    print(f"Durée totale (2 flux en parallèle) : {elapsed_s:.1f}s")
    for label, lines in zip(("A", "B"), results):
        if not lines:
            print(f"WARNING: processor {label} n'a produit aucune ligne — deadlock/croisement possible.")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Harnais isolé (ADR-0044) : VRAM + sécurité concurrente de 2 AudioProcessor "
        "sur 1 TranscriptionEngine partagé, sans séparation de voix réelle (2 fichiers distincts, "
        "à lancer avant toute réécriture de harness_pipeline.py)."
    )
    parser.add_argument("corpus_key_a", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("corpus_key_b", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    args = parser.parse_args()

    asyncio.run(run_benchmark(args.corpus_key_a, args.corpus_key_b, args.corpus_dir))


if __name__ == "__main__":
    main()
