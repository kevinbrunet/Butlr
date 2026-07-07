#!/usr/bin/env python3
"""Training script for 'Hey Carson' wake word — nanowakeword edition.

Flux :
  Phase 0 : charge training_config.yaml depuis /data/
  Phase 1 : construit le YAML nanowakeword dans /work/
  Phase 2 : lance nanowakeword CLI (-G -t -T -d = generate + transform + train + distill)
  Phase 3 : copie hey_carson.onnx dans /data/
"""
from __future__ import annotations

import logging
import shutil
import subprocess
import sys
import yaml
from pathlib import Path

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    stream=sys.stdout,
    force=True,
)
log = logging.getLogger("train")

DATA_DIR = Path("/data")   # volume host : carson/assets/wakeword/
WORK_DIR = Path("/work")   # répertoire de travail nanowakeword
NWW_CONFIG = WORK_DIR / "nww_config.yaml"


def load_our_config() -> dict:
    cfg_path = DATA_DIR / "training_config.yaml"
    if not cfg_path.exists():
        log.error("training_config.yaml absent de /data/")
        log.error("Lance d'abord : ./scripts/train-wakeword.sh --generate-config")
        sys.exit(1)
    with open(cfg_path, encoding="utf-8") as f:
        return yaml.safe_load(f)


def build_nww_config(our: dict) -> Path:
    """Construit le YAML au format nanowakeword à partir de notre config."""
    WORK_DIR.mkdir(parents=True, exist_ok=True)

    positive_dir = WORK_DIR / "positive"
    negative_dir = WORK_DIR / "negative"
    positive_dir.mkdir(parents=True, exist_ok=True)
    negative_dir.mkdir(parents=True, exist_ok=True)

    # Copie les enregistrements réels si présents (comble le gap TTS/voix humaine).
    custom_dir = our.get("custom_positive_clips_dir", "")
    if custom_dir:
        recordings = DATA_DIR / custom_dir
        wavs = sorted(recordings.glob("*.wav")) if recordings.exists() else []
        if wavs:
            for src in wavs:
                shutil.copy2(src, positive_dir / f"real_{src.name}")
            log.info("Clips réels injectés dans positive/ : %d fichiers", len(wavs))
        else:
            log.warning("custom_positive_clips_dir=%s spécifié mais dossier vide", recordings)

    # ~ Format YAML nanowakeword>=2.0 — valider contre CONFIGURATION_GUIDE.md si la version change.
    cfg = {
        "model_name": our.get("model_name", "hey_carson"),
        "model_type": our.get("model_type", "lstm"),
        "output_dir": str(WORK_DIR / "trained_models"),
        "target_phrase": our.get("target_phrase", "Hey Carson"),
        "positive_data_path": str(positive_dir),
        "negative_data_path": str(negative_dir),
        "n_epochs": our.get("n_epochs", 100),
        "detection_threshold": our.get("detection_threshold", 0.5),
        "generate_clips": True,
        "transform_clips": True,
        "train_model": True,
        "distill": True,
    }

    with open(NWW_CONFIG, "w", encoding="utf-8") as f:
        yaml.dump(cfg, f, default_flow_style=False)

    log.info("Config nanowakeword écrite : %s", NWW_CONFIG)
    return NWW_CONFIG


def run_training(config_path: Path) -> None:
    # ~ CLI nanowakeword>=2.0 : nanowakeword -c config.yaml -G -t -T -d
    result = subprocess.run(
        ["nanowakeword", "-c", str(config_path), "-G", "-t", "-T", "-d"],
        cwd=str(WORK_DIR),
        check=False,
    )
    if result.returncode != 0:
        log.error("nanowakeword training échoué (exit %d)", result.returncode)
        sys.exit(result.returncode)


def collect_output(model_name: str) -> None:
    trained_dir = WORK_DIR / "trained_models" / model_name

    # ~ nanowakeword place le modèle dans output_dir/model_name/ — à confirmer sur la version installée.
    candidates = list(trained_dir.rglob(f"{model_name}*.onnx")) if trained_dir.exists() else []
    if not candidates:
        # fallback : cherche dans tout /work
        candidates = list(WORK_DIR.rglob(f"{model_name}*.onnx"))

    if not candidates:
        log.error("Aucun .onnx trouvé pour %s dans %s", model_name, WORK_DIR)
        log.error("Contenu /work : %s", list(WORK_DIR.rglob("*.onnx")))
        sys.exit(1)

    # Préfère le modèle plein (pas le lite/distilled si plusieurs fichiers).
    # ~ Convention de nommage nanowakeword : hey_carson.onnx / hey_carson_lite.onnx.
    main_model = next((p for p in candidates if "_lite" not in p.name), candidates[0])
    dst = DATA_DIR / f"{model_name}.onnx"
    shutil.copy2(main_model, dst)
    log.info("Modèle prêt : %s (%d KB)", dst, dst.stat().st_size // 1024)

    lite = next((p for p in candidates if "_lite" in p.name), None)
    if lite:
        dst_lite = DATA_DIR / f"{model_name}_lite.onnx"
        shutil.copy2(lite, dst_lite)
        log.info("Modèle lite : %s (%d KB)", dst_lite, dst_lite.stat().st_size // 1024)

    log.info("")
    log.info("Active le wake word :")
    log.info("  export USE_WAKEWORD=1")
    log.info("  carson")


if __name__ == "__main__":
    log.info("=== Entraînement wake word 'Hey Carson' (nanowakeword) ===")

    our_config = load_our_config()
    log.info("Config source : %s", our_config)

    nww_config = build_nww_config(our_config)

    log.info("--- Lancement nanowakeword (generate + transform + train + distill) ---")
    run_training(nww_config)

    log.info("--- Collecte de la sortie ---")
    collect_output(our_config.get("model_name", "hey_carson"))
