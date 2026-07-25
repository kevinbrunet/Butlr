from __future__ import annotations

import argparse
import asyncio
import wave
from pathlib import Path

from whisperlivekit import AudioProcessor, TranscriptionEngine


async def transcribe_wav(wav_path: Path, lan: str) -> list[dict]:
    """Transcrit `wav_path` (attendu PCM16 mono 16kHz, cf. `corpus.EXPECTED_SAMPLE_RATE_HZ` —
    resampler d'abord au besoin, ex. `ffmpeg -i in.wav -ar 16000 -ac 1 -sample_fmt s16
    out.wav`) via WhisperLiveKit. Sonde ad hoc pour vérifier le texte produit par un WAV de
    sortie TTS (ex. `harness_tts_continuation.py`) sans avoir à réécouter tout le fichier —
    pas de diarisation (une seule voix sur ces WAV).
    """
    engine = TranscriptionEngine(pcm_input=True, diarization=False, lan=lan)
    processor = AudioProcessor(transcription_engine=engine, mode="full")
    last_lines: list[dict] = []

    async def consume() -> None:
        nonlocal last_lines
        results = await processor.create_tasks()
        async for response in results:
            last_lines = response.to_dict().get("lines", [])

    consumer_task = asyncio.create_task(consume())
    with wave.open(str(wav_path), "rb") as wav_file:
        frames = wav_file.readframes(1600)
        while frames:
            await processor.process_audio(frames)
            frames = wav_file.readframes(1600)
    await processor.process_audio(b"")
    await asyncio.sleep(3.0)
    consumer_task.cancel()
    return last_lines


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Transcrit un WAV (PCM16 mono 16kHz) via WhisperLiveKit et affiche le "
        "texte obtenu — sonde ad hoc pour relire la sortie d'un WAV TTS (ex. "
        "harness_tts_continuation.py) sans diarisation."
    )
    parser.add_argument("wav_path", type=Path)
    parser.add_argument("--lan", default="fr", help="Langue source pour WLK (défaut : fr).")
    args = parser.parse_args()

    lines = asyncio.run(transcribe_wav(args.wav_path, args.lan))
    for line in lines:
        print(line.get("text"))


if __name__ == "__main__":
    main()
