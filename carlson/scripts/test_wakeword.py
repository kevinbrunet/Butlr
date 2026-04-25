#!/usr/bin/env python3
"""Test rapide du modèle wake word — affiche les scores en temps réel.

Usage :
    python carlson/scripts/test_wakeword.py
    python carlson/scripts/test_wakeword.py --model assets/wakeword/hey_carlson.onnx
    python carlson/scripts/test_wakeword.py --threshold 0.3

Dis "Hey Carlson" et observe les scores monter. Ctrl+C pour quitter.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Test wake word en temps réel")
    parser.add_argument(
        "--model",
        default=str(Path(__file__).parent.parent / "assets" / "wakeword" / "hey_carlson.onnx"),
        help="Chemin vers le modèle .tflite ou .onnx",
    )
    parser.add_argument("--threshold", type=float, default=0.5, help="Seuil de détection")
    parser.add_argument("--framework", default="onnx", choices=["tflite", "onnx"])
    args = parser.parse_args()

    model_path = Path(args.model)
    if not model_path.exists():
        print(f"ERREUR : modèle introuvable : {model_path}", file=sys.stderr)
        sys.exit(1)

    try:
        import pyaudio  # type: ignore[import-untyped]
    except ImportError:
        print("pyaudio manquant : pip install pyaudio", file=sys.stderr)
        sys.exit(1)

    try:
        import openwakeword.utils
        import openwakeword
        resources = Path(openwakeword.utils.__file__).parent / "resources" / "models"
        if not (resources / "melspectrogram.onnx").exists():
            print("Téléchargement des modèles utilitaires OWW...")
            openwakeword.utils.download_models()
        from openwakeword.model import Model
    except ImportError:
        print("openwakeword manquant : pip install openwakeword", file=sys.stderr)
        sys.exit(1)

    print(f"Chargement : {model_path}")
    model = Model(wakeword_models=[str(model_path)], inference_framework=args.framework)

    RATE = 16000
    CHUNK = 1280  # 80 ms
    pa = pyaudio.PyAudio()
    stream = pa.open(rate=RATE, channels=1, format=pyaudio.paInt16,
                     input=True, frames_per_buffer=CHUNK)

    import numpy as np

    print(f"\nÉcoute... (seuil={args.threshold}) — dis 'Hey Carlson' — Ctrl+C pour quitter\n")
    try:
        while True:
            raw = stream.read(CHUNK, exception_on_overflow=False)
            pcm = np.frombuffer(raw, dtype=np.int16)
            # OWW attend du float32 normalisé [-1, 1] (même conversion que WakeWordProcessor)
            audio = pcm.astype(np.float32) / 32768.0

            # VU-mètre : amplitude RMS pour vérifier que le micro capte quelque chose
            rms = float(np.sqrt(np.mean(audio ** 2)))
            vu = "▓" * int(rms * 200)

            scores: dict = model.predict(audio)
            for name, score in scores.items():
                bar = "█" * int(score * 30)
                trigger = " ← DÉTECTÉ !" if score >= args.threshold else ""
                print(
                    f"\r{name}: {score:.3f} |{bar:<30}|{trigger}  "
                    f"  mic: {rms:.4f} |{vu:<20}|    ",
                    end="", flush=True,
                )
    except KeyboardInterrupt:
        print("\n\nArrêt.")
    finally:
        stream.stop_stream()
        stream.close()
        pa.terminate()


if __name__ == "__main__":
    main()
