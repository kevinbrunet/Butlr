"""Butlr / Phase 1 — smoke test faster-whisper sur un WAV court.

Usage :
    # depuis un venv avec faster-whisper installé :
    #   python -m venv .venv
    #   .venv\Scripts\Activate.ps1
    #   pip install faster-whisper
    #
    python Test-Whisper.py <chemin-vers-wav>

Variables d'env lues (avec fallback) :
    WHISPER_MODEL         (défaut : large-v3)
    WHISPER_DEVICE        (défaut : cuda)
    WHISPER_COMPUTE_TYPE  (défaut : float16)

Au premier lancement, faster-whisper télécharge le modèle (~3 GB pour large-v3 ~).
Le cache HF par défaut est ~/.cache/huggingface/hub (ou %USERPROFILE%\.cache\...).
"""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path


def main() -> int:
    if len(sys.argv) < 2:
        print("Usage: python Test-Whisper.py <path-to-wav>", file=sys.stderr)
        return 2

    wav_path = Path(sys.argv[1])
    if not wav_path.exists():
        print(f"Fichier introuvable : {wav_path}", file=sys.stderr)
        return 2

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        print(
            "faster-whisper non installé. Dans un venv : pip install faster-whisper",
            file=sys.stderr,
        )
        return 2

    model_name   = os.environ.get("WHISPER_MODEL", "large-v3")
    device       = os.environ.get("WHISPER_DEVICE", "cuda")
    compute_type = os.environ.get("WHISPER_COMPUTE_TYPE", "float16")

    print(f"[..] Chargement {model_name} ({device}, {compute_type})...")
    t0 = time.perf_counter()
    model = WhisperModel(model_name, device=device, compute_type=compute_type)
    t_load = time.perf_counter() - t0
    print(f"[ok] Modèle chargé en {t_load:.1f}s")

    print(f"[..] Transcription : {wav_path}")
    t0 = time.perf_counter()
    segments, info = model.transcribe(str(wav_path), beam_size=5)
    # segments est un générateur — on force l'itération pour chronométrer.
    seg_list = list(segments)
    t_trans = time.perf_counter() - t0

    print(f"[ok] Langue détectée : {info.language} (p={info.language_probability:.2f})")
    print(f"[ok] Transcription en {t_trans:.1f}s ({info.duration:.1f}s audio)")
    print("---")
    for s in seg_list:
        print(f"[{s.start:6.2f} -> {s.end:6.2f}] {s.text}")
    print("---")

    return 0


if __name__ == "__main__":
    sys.exit(main())
