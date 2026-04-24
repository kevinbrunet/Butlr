"""Butlr / Phase 1 — smoke test faster-whisper sur un WAV court.

Usage :
    # depuis le venv carlson (pip install -e .[all]) :
    python Test-Whisper.py <chemin-vers-wav>

Variables d'env lues (avec fallback) :
    STT_MODEL             chemin absolu vers un dossier CTranslate2 local,
                          ou nom HF ("large-v3"). Défaut = ~/butlr-env/models/whisper/faster-whisper-large-v3.
                          Valorisé automatiquement par env.ps1 (via Get-WhisperModel.ps1).
    WHISPER_DEVICE        (défaut : cuda)
    WHISPER_COMPUTE_TYPE  (défaut : float16)

Le modèle doit être téléchargé au préalable avec Get-WhisperModel.ps1.
Aucun accès réseau n'est effectué si STT_MODEL pointe sur un dossier local valide.
"""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path


def _default_model() -> str:
    butlr_env = os.environ.get("BUTLR_ENV_DIR", str(Path.home() / "butlr-env"))
    return str(Path(butlr_env) / "models" / "whisper" / "faster-whisper-large-v3")


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
            "faster-whisper non installé. Dans le venv carlson : pip install -e .[all]",
            file=sys.stderr,
        )
        return 2

    model_path   = os.environ.get("STT_MODEL", _default_model())
    device       = os.environ.get("WHISPER_DEVICE", "cuda")
    compute_type = os.environ.get("WHISPER_COMPUTE_TYPE", "float16")

    print(f"[..] Chargement {model_path} ({device}, {compute_type})...")
    t0 = time.perf_counter()
    model = WhisperModel(model_path, device=device, compute_type=compute_type, local_files_only=True)
    t_load = time.perf_counter() - t0
    print(f"[ok] Modèle chargé en {t_load:.1f}s ({model_path})")

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
