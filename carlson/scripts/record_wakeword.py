#!/usr/bin/env python3
"""Enregistre des clips "Hey Carlson" pour le training OWW.

Usage :
    python carlson/scripts/record_wakeword.py
    python carlson/scripts/record_wakeword.py --count 50 --out assets/wakeword/my_recordings

Chaque appui sur Entrée enregistre 2 secondes.
Vise 30-50 clips : variations de vitesse, d'intonation, distance micro.
Format de sortie : WAV 16kHz mono 16-bit (format requis par openWakeWord).
"""
from __future__ import annotations

import argparse
import wave
from pathlib import Path

RATE = 16_000
CHANNELS = 1
SAMPLE_WIDTH = 2   # 16-bit
DURATION_S = 2     # secondes par clip


def record_clip(pa: "pyaudio.PyAudio", duration: float) -> bytes:
    import pyaudio
    stream = pa.open(
        rate=RATE, channels=CHANNELS,
        format=pyaudio.paInt16, input=True,
        frames_per_buffer=1024,
    )
    frames = []
    n_chunks = int(RATE / 1024 * duration)
    for _ in range(n_chunks):
        frames.append(stream.read(1024, exception_on_overflow=False))
    stream.stop_stream()
    stream.close()
    return b"".join(frames)


def save_wav(path: Path, data: bytes) -> None:
    with wave.open(str(path), "wb") as f:
        f.setnchannels(CHANNELS)
        f.setsampwidth(SAMPLE_WIDTH)
        f.setframerate(RATE)
        f.writeframes(data)


def main() -> None:
    parser = argparse.ArgumentParser(description="Enregistre des clips wake word")
    parser.add_argument("--count", type=int, default=40, help="Nombre de clips à enregistrer")
    parser.add_argument(
        "--out",
        type=Path,
        default=Path(__file__).parent.parent / "assets" / "wakeword" / "my_recordings",
    )
    parser.add_argument("--duration", type=float, default=DURATION_S, help="Durée en secondes")
    args = parser.parse_args()

    try:
        import pyaudio
    except ImportError:
        print("pyaudio manquant : pip install pyaudio")
        return

    args.out.mkdir(parents=True, exist_ok=True)
    # Reprend la numérotation si des clips existent déjà
    existing = len(list(args.out.glob("*.wav")))

    pa = pyaudio.PyAudio()
    print(f"\nSortie     : {args.out}")
    print(f"Clips cible : {args.count}  (déjà enregistrés : {existing})")
    print(f"Durée/clip  : {args.duration}s\n")
    print("Conseils :")
    print("  - Varie l'intonation (question, affirmation, naturel)")
    print("  - Varie la distance au micro (30 cm, 1 m, 2 m)")
    print("  - Varie la vitesse (rapide, lent, normal)")
    print("  - Quelques clips avec bruit de fond (TV, musique faible)\n")

    recorded = 0
    idx = existing
    try:
        while recorded < args.count:
            remaining = args.count - recorded
            prompt = input(f"[{recorded+1}/{args.count}] Entrée pour enregistrer (q = quitter) : ")
            if prompt.strip().lower() == "q":
                break
            print(f"  🎙  Parle maintenant ({args.duration}s)...", end="", flush=True)
            data = record_clip(pa, args.duration)
            dest = args.out / f"hey_carlson_{idx:04d}.wav"
            save_wav(dest, data)
            idx += 1
            recorded += 1
            print(f"  ✓  {dest.name}")
    except KeyboardInterrupt:
        print("\nInterrompu.")
    finally:
        pa.terminate()

    total = existing + recorded
    print(f"\n{recorded} clips enregistrés → {args.out}  (total : {total})")
    if total < 20:
        print("⚠  Moins de 20 clips — vise au moins 30 pour un bon modèle.")
    elif total < 30:
        print("~ Correct — 40-50 clips donneront de meilleurs résultats.")
    else:
        print("✓  Bon nombre de clips.")
    print("\nProchaine étape : relance l'entraînement")
    print("  .\\scripts\\Train-WakeWord.ps1 -Gpu")


if __name__ == "__main__":
    main()
