#!/usr/bin/env python3
"""Évaluation du modèle wake word sur les fichiers WAV de my_recordings/.

Lit chaque .wav, le passe chunk par chunk dans le modèle (identique au pipeline
WakeWordProcessor), et rapporte le score max, la détection et les faux négatifs.

Usage :
    python carson/scripts/eval_wakeword.py
    python carson/scripts/eval_wakeword.py --dir carson/assets/wakeword/my_recordings
    python carson/scripts/eval_wakeword.py --threshold 0.3 --confirmation 1

    # Diagnostic : tester avec un modèle built-in OWW (hey_jarvis, alexa, …)
    # pour vérifier que le script lui-même fonctionne.
    python carson/scripts/eval_wakeword.py --builtin hey_jarvis
"""
from __future__ import annotations

import argparse
import sys
import wave
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

# Même constantes que WakeWordProcessor
_CHUNK_SAMPLES = 1280
_TARGET_RATE = 16_000


@dataclass
class FileResult:
    name: str
    group: str
    duration_s: float
    max_score: float
    detected: bool
    chunks_above: int
    scores: list[float] = field(default_factory=list, repr=False)


def load_wav_as_int16(path: Path) -> tuple[np.ndarray, int]:
    """Retourne (samples int16, sample_rate).

    OWW exige du int16 brut dans _get_melspectrogram() — passer du float32
    normalisé [-1,1] entraîne un cast silencieux vers 0 (toutes les valeurs
    tronquées) et des scores proches de zéro.
    """
    with wave.open(str(path), "rb") as wf:
        rate = wf.getframerate()
        n_channels = wf.getnchannels()
        sampwidth = wf.getsampwidth()
        n_frames = wf.getnframes()
        raw = wf.readframes(n_frames)

    if sampwidth == 2:
        pcm = np.frombuffer(raw, dtype=np.int16)
    elif sampwidth == 4:
        pcm = np.frombuffer(raw, dtype=np.int32)
        pcm = (pcm >> 16).astype(np.int16)
    else:
        raise ValueError(f"sampwidth={sampwidth} non supporté")

    if n_channels > 1:
        pcm = pcm.reshape(-1, n_channels).mean(axis=1).astype(np.int16)

    return pcm, rate


def resample_int16(samples: np.ndarray, from_rate: int, to_rate: int) -> np.ndarray:
    """Rééchantillonnage int16 via float64 intermédiaire (évite overflow)."""
    if from_rate == to_rate:
        return samples
    ratio = to_rate / from_rate
    new_len = int(len(samples) * ratio)
    indices = np.linspace(0, len(samples) - 1, new_len)
    resampled = np.interp(indices, np.arange(len(samples)), samples.astype(np.float64))
    return np.clip(resampled, -32768, 32767).astype(np.int16)


def evaluate_file(path: Path, model: object, threshold: float, confirmation: int) -> FileResult:
    samples, rate = load_wav_as_int16(path)
    if rate != _TARGET_RATE:
        samples = resample_int16(samples, rate, _TARGET_RATE)

    duration_s = len(samples) / _TARGET_RATE

    scores: list[float] = []
    consec = 0
    detected = False

    for start in range(0, len(samples) - _CHUNK_SAMPLES + 1, _CHUNK_SAMPLES):
        chunk = samples[start : start + _CHUNK_SAMPLES]
        raw_scores: dict[str, float] = model.predict(chunk)  # type: ignore[attr-defined]
        score = max(raw_scores.values()) if raw_scores else 0.0
        scores.append(score)

        if score >= threshold:
            consec += 1
            if consec >= confirmation:
                detected = True
        else:
            consec = 0

    group = _group_name(path.stem)
    return FileResult(
        name=path.name,
        group=group,
        duration_s=duration_s,
        max_score=max(scores) if scores else 0.0,
        detected=detected,
        chunks_above=sum(1 for s in scores if s >= threshold),
        scores=scores,
    )


def _group_name(stem: str) -> str:
    """Extrait le préfixe de groupe depuis le nom de fichier sans numéro."""
    parts = stem.rsplit("_", 1)
    if len(parts) == 2 and parts[1].isdigit():
        return parts[0]
    return stem


def _bar(value: float, width: int = 20) -> str:
    filled = int(value * width)
    return "█" * filled + "░" * (width - filled)


def main() -> None:
    default_dir = Path(__file__).parent.parent / "assets" / "wakeword" / "my_recordings"
    default_model = Path(__file__).parent.parent / "assets" / "wakeword" / "hey_carson.onnx"

    parser = argparse.ArgumentParser(description="Évaluation wake word par fichier WAV")
    parser.add_argument("--dir", default=str(default_dir), help="Dossier de fichiers WAV")
    parser.add_argument("--model", default=str(default_model), help="Chemin .onnx ou .tflite")
    parser.add_argument("--threshold", type=float, default=0.5, help="Seuil de détection")
    parser.add_argument(
        "--confirmation",
        type=int,
        default=2,
        help="Chunks consécutifs ≥ seuil requis (défaut=2, comme WakeWordProcessor)",
    )
    parser.add_argument("--framework", default="onnx", choices=["tflite", "onnx"])
    parser.add_argument("--verbose", action="store_true", help="Afficher scores par chunk")
    parser.add_argument(
        "--builtin",
        metavar="NAME",
        help="Charger un modèle built-in OWW par nom (ex: hey_jarvis, alexa) "
             "pour vérifier que le script fonctionne indépendamment de hey_carson",
    )
    parser.add_argument(
        "--no-reset",
        action="store_true",
        help="Ne pas réinitialiser le modèle entre les fichiers "
             "(état mel accumulé en continu, comme en production)",
    )
    args = parser.parse_args()

    wav_dir = Path(args.dir)
    model_path = Path(args.model)

    if not wav_dir.is_dir():
        print(f"ERREUR : dossier introuvable : {wav_dir}", file=sys.stderr)
        sys.exit(1)
    if not model_path.exists():
        print(f"ERREUR : modèle introuvable : {model_path}", file=sys.stderr)
        sys.exit(1)

    wav_files = sorted(wav_dir.glob("*.wav"))
    if not wav_files:
        print(f"Aucun .wav dans {wav_dir}", file=sys.stderr)
        sys.exit(1)

    try:
        import openwakeword.utils
        resources = Path(openwakeword.utils.__file__).parent / "resources" / "models"
        if not (resources / "melspectrogram.onnx").exists():
            print("Téléchargement des modèles utilitaires OWW...")
            openwakeword.utils.download_models()
        from openwakeword.model import Model
    except ImportError:
        print("openwakeword manquant : pip install openwakeword", file=sys.stderr)
        sys.exit(1)

    if args.builtin:
        print(f"Chargement modèle built-in OWW : {args.builtin}")
        model = Model(wakeword_models=[args.builtin], inference_framework=args.framework)
    else:
        print(f"Chargement modèle : {model_path}")
        model = Model(wakeword_models=[str(model_path)], inference_framework=args.framework)

    print(f"Dossier        : {wav_dir}")
    print(f"Seuil          : {args.threshold}  |  Confirmation : {args.confirmation} chunk(s)")
    print(f"Fichiers WAV   : {len(wav_files)}\n")

    results: list[FileResult] = []
    for i, wav in enumerate(wav_files, 1):
        print(f"  [{i:3d}/{len(wav_files)}] {wav.name} ...", end="\r", flush=True)

        if not args.no_reset:
            # Réinitialiser l'état interne du modèle entre chaque fichier.
            # ~ OWW maintient un buffer de frames mel internes ; reset() repart de zéro.
            # Sans reset, le contexte mel s'accumule en continu (comportement production).
            try:
                model.reset()
            except AttributeError:
                pass

        result = evaluate_file(wav, model, args.threshold, args.confirmation)
        results.append(result)

        if args.verbose:
            scores_str = " ".join(f"{s:.2f}" for s in result.scores)
            print(f"  {wav.name}: {scores_str}")

    print(" " * 80)

    # ---- Tableau par fichier ----
    col_name = max(len(r.name) for r in results)
    header = f"{'Fichier':{col_name}}  {'Durée':>6}  {'MaxScore':>8}  {'Chunks≥seuil':>12}  {'Détecté':>8}"
    print(header)
    print("-" * len(header))

    for r in results:
        icon = "✓" if r.detected else "✗"
        print(
            f"{r.name:{col_name}}  {r.duration_s:>5.1f}s  {r.max_score:>8.4f}  "
            f"{r.chunks_above:>12d}  {icon} {_bar(r.max_score, 16)}"
        )

    # ---- Résumé par groupe ----
    print()
    by_group: dict[str, list[FileResult]] = defaultdict(list)
    for r in results:
        by_group[r.group].append(r)

    total_detected = sum(1 for r in results if r.detected)
    print(f"{'RÉSUMÉ PAR GROUPE':=^60}")
    for group, group_results in sorted(by_group.items()):
        n = len(group_results)
        detected = sum(1 for r in group_results if r.detected)
        avg_max = sum(r.max_score for r in group_results) / n
        peak = max(r.max_score for r in group_results)
        rate = detected / n * 100
        print(
            f"  {group:<40}  {detected:>3}/{n:<3}  ({rate:5.1f}%)  avg={avg_max:.4f}  peak={peak:.4f}"
        )

    print("-" * 60)
    rate_total = total_detected / len(results) * 100
    avg_all = sum(r.max_score for r in results) / len(results)
    peak_all = max(r.max_score for r in results)
    print(f"  {'TOTAL':<40}  {total_detected:>3}/{len(results):<3}  ({rate_total:5.1f}%)  avg={avg_all:.4f}  peak={peak_all:.4f}")

    # ---- Top 10 scores (utile pour diagnostiquer un modèle faible) ----
    top10 = sorted(results, key=lambda r: r.max_score, reverse=True)[:10]
    print(f"\nTop 10 scores (candidats les plus proches du seuil {args.threshold}) :")
    for r in top10:
        icon = "✓" if r.detected else "✗"
        print(f"  {icon} {r.name:<50}  {r.max_score:.4f}")

    # ---- Faux négatifs ----
    false_negatives = [r for r in results if not r.detected]
    if false_negatives:
        print(f"\n⚠  Faux négatifs ({len(false_negatives)}) :")
        for r in false_negatives:
            print(f"   {r.name}  max_score={r.max_score:.4f}")
    else:
        print("\n✓ Tous les fichiers détectés.")


if __name__ == "__main__":
    main()
