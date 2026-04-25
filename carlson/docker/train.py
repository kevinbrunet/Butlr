#!/usr/bin/env python3
"""Training script for 'Hey Carlson' wake word.

Suit exactement le notebook automatic_model_training.ipynb d'openWakeWord :
  Phase 0 : telechargement des features pre-calculees (ACAV100M + validation)
  Phase 1 : generation des clips positifs via piper-sample-generator  (--generate_clips)
  Phase 2 : augmentation des clips                                     (--augment_clips)
  Phase 3 : entrainement du modele                                     (--train_model)
  Phase 4 : copie du .tflite / .onnx dans /data/
"""
from __future__ import annotations

import logging
import os
import shutil
import subprocess
import sys
import urllib.request
import yaml
from pathlib import Path

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    stream=sys.stdout,
    force=True,
)
log = logging.getLogger("train")

# ---------------------------------------------------------------------------
# Chemins fixes dans le container
# ---------------------------------------------------------------------------
DATA_DIR     = Path("/data")          # volume host : carlson/assets/wakeword/
WORK_DIR     = Path("/work")          # répertoire de travail temporaire
OWW_DIR      = Path("/oww")           # clone openWakeWord
PIPER_GEN    = Path("/piper-gen")     # clone piper-sample-generator

OWW_TRAIN_SCRIPT = OWW_DIR / "openwakeword" / "train.py"
OWW_EXAMPLE_CFG  = OWW_DIR / "examples" / "custom_model.yml"

# Features pré-calculées (négatifs) — téléchargées depuis HuggingFace
# ~ Taille approximative : quelques GB pour ACAV100M, ~100 MB pour validation
FEATURES_DIR  = WORK_DIR / "features"
FEATURES_URLS = {
    "openwakeword_features_ACAV100M_2000_hrs_16bit.npy": (
        "https://huggingface.co/datasets/davidscripka/openwakeword_features"
        "/resolve/main/openwakeword_features_ACAV100M_2000_hrs_16bit.npy"
    ),
    "validation_set_features.npy": (
        "https://huggingface.co/datasets/davidscripka/openwakeword_features"
        "/resolve/main/validation_set_features.npy"
    ),
}

OWW_CONFIG_PATH = WORK_DIR / "my_model.yaml"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def load_our_config() -> dict:
    """Charge training_config.yaml depuis /data/."""
    if not (DATA_DIR / "training_config.yaml").exists():
        log.error("training_config.yaml absent de /data/")
        log.error("Lance d'abord : .\\scripts\\Train-WakeWord.ps1 -GenerateConfig")
        sys.exit(1)
    with open(DATA_DIR / "training_config.yaml", encoding="utf-8") as f:
        return yaml.safe_load(f)


def build_oww_config(our: dict) -> Path:
    """Construit le YAML de config au format openWakeWord à partir de notre config.

    Charge le template depuis /oww/examples/custom_model.yml et surcharge
    avec nos valeurs, suivant exactement le Cell 15 du notebook.
    """
    if not OWW_EXAMPLE_CFG.exists():
        log.error("Config exemple OWW absente : %s", OWW_EXAMPLE_CFG)
        sys.exit(1)

    with open(OWW_EXAMPLE_CFG, encoding="utf-8") as f:
        cfg = yaml.safe_load(f)

    phrase      = our.get("target_phrase", "Hey Carlson")
    model_name  = our.get("model_name", "hey_carlson")
    n_samples   = our.get("n_positive_samples", 1000)
    n_epochs    = our.get("n_epochs", 100)

    cfg["target_phrase"] = [phrase]
    cfg["model_name"]    = model_name
    cfg["n_samples"]     = n_samples
    cfg["n_samples_val"] = max(100, n_samples // 10)
    cfg["steps"]         = n_epochs * 100   # ~ conversion epochs -> steps

    cfg["target_accuracy"] = 0.6
    cfg["target_recall"]   = 0.25

    # Données pré-calculées (pas de téléchargement audioset/FMA)
    cfg["feature_data_files"] = {
        "ACAV100M": str(FEATURES_DIR / "openwakeword_features_ACAV100M_2000_hrs_16bit.npy")
    }
    cfg["false_positive_validation_data_path"] = str(
        FEATURES_DIR / "validation_set_features.npy"
    )
    cfg["background_paths"] = []

    # Chemin vers piper-sample-generator
    if "piper_sample_generator" in cfg:
        cfg["piper_sample_generator"]["path"]           = str(PIPER_GEN)
        cfg["piper_sample_generator"]["speaker_model"]  = str(
            PIPER_GEN / "models" / "en_US-libritts_r-medium.pt"
        )

    WORK_DIR.mkdir(parents=True, exist_ok=True)
    with open(OWW_CONFIG_PATH, "w", encoding="utf-8") as f:
        yaml.dump(cfg, f, default_flow_style=False)

    log.info("Config OWW ecrite : %s", OWW_CONFIG_PATH)
    log.debug("Contenu : %s", cfg)
    return OWW_CONFIG_PATH


def download_features() -> None:
    """Telecharge les features pre-calculees si absentes.

    ~ Taille : quelques GB pour ACAV100M, ~100 MB pour validation.
      Premier lancement : peut prendre 10-30 min selon la connexion.
    """
    FEATURES_DIR.mkdir(parents=True, exist_ok=True)
    for filename, url in FEATURES_URLS.items():
        dest = FEATURES_DIR / filename
        if dest.exists():
            log.info("Features en cache : %s", filename)
            continue
        log.info("Telechargement %s ...", filename)
        log.info("  (peut prendre plusieurs minutes)")
        urllib.request.urlretrieve(url, dest)
        size_mb = dest.stat().st_size // (1024 * 1024)
        log.info("  -> %s (%d MB)", dest, size_mb)


def verify_piper_gen() -> None:
    """Vérifie que generate_samples.py est accessible dans piper-sample-generator."""
    gs = PIPER_GEN / "generate_samples.py"
    if not PIPER_GEN.exists():
        log.error("Répertoire piper-gen absent : %s", PIPER_GEN)
        sys.exit(1)
    log.info("Contenu de %s : %s", PIPER_GEN, [p.name for p in PIPER_GEN.iterdir()])
    if not gs.exists():
        log.error("generate_samples.py introuvable dans %s", PIPER_GEN)
        sys.exit(1)
    log.info("generate_samples.py trouvé : OK")
    # Test d'import direct pour révéler une éventuelle erreur de dépendance
    result = subprocess.run(
        [sys.executable, "-c", f"import sys; sys.path.insert(0, '{PIPER_GEN}'); import generate_samples; print('import OK')"],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        log.error("Import generate_samples échoué :\n%s\n%s", result.stdout, result.stderr)
        sys.exit(1)
    log.info("import generate_samples : OK")


def run_phase(label: str, flag: str) -> None:
    """Exécute une phase du script train.py d'openWakeWord."""
    log.info("=== %s ===", label)
    env = os.environ.copy()
    existing = env.get("PYTHONPATH", "")
    env["PYTHONPATH"] = f"{PIPER_GEN}:{existing}" if existing else str(PIPER_GEN)
    log.info("  PYTHONPATH=%s", env["PYTHONPATH"])
    result = subprocess.run(
        [sys.executable, str(OWW_TRAIN_SCRIPT), "--training_config", str(OWW_CONFIG_PATH), flag],
        cwd=str(WORK_DIR),
        check=False,
        env=env,
    )
    if result.returncode != 0:
        log.error("Phase '%s' echouee (exit %d)", label, result.returncode)
        sys.exit(result.returncode)


def collect_output(model_name: str) -> None:
    """Copie .onnx et .tflite depuis my_custom_model/ vers /data/."""
    src_dir = WORK_DIR / "my_custom_model"
    found = False

    for ext in (".onnx", ".tflite"):
        src = src_dir / f"{model_name}{ext}"
        if src.exists():
            dst = DATA_DIR / f"{model_name}{ext}"
            shutil.copy2(src, dst)
            log.info("Copie : %s -> %s (%d KB)", src.name, dst, dst.stat().st_size // 1024)
            found = True

    if not found:
        log.warning("Aucun modele trouve dans %s", src_dir)
        log.warning("Contenu /work : %s", list(WORK_DIR.iterdir()))

    tflite = DATA_DIR / f"{model_name}.tflite"
    if tflite.exists():
        log.info("Succes ! Modele pret : %s (%d KB)", tflite, tflite.stat().st_size // 1024)
        log.info("")
        log.info("Active le wake word :")
        log.info("  $env:USE_WAKEWORD = '1'")
        log.info("  carlson")
    else:
        onnx = DATA_DIR / f"{model_name}.onnx"
        if onnx.exists():
            log.warning(
                ".tflite absent mais .onnx present (%s). "
                "Conversion manuelle necessaire (voir convert_onnx_to_tflite dans le notebook).",
                onnx,
            )
        else:
            log.error("Aucun modele produit. Verifier les logs ci-dessus.")
            sys.exit(1)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    log.info("=== Entrainement wake word 'Hey Carlson' ===")
    log.info("Script OWW : %s", OWW_TRAIN_SCRIPT)

    if not OWW_TRAIN_SCRIPT.exists():
        log.error("train.py openWakeWord introuvable : %s", OWW_TRAIN_SCRIPT)
        sys.exit(1)

    our_config = load_our_config()
    log.info("Config source : %s", our_config)

    oww_config = build_oww_config(our_config)

    log.info("--- Phase 0 : telechargement features pre-calculees ---")
    download_features()

    log.info("--- Phase 1 : generation des clips positifs (piper-sample-generator) ---")
    verify_piper_gen()
    run_phase("generate_clips", "--generate_clips")

    log.info("--- Phase 2 : augmentation des clips ---")
    run_phase("augment_clips", "--augment_clips")

    log.info("--- Phase 3 : entrainement du modele ---")
    run_phase("train_model", "--train_model")

    log.info("--- Phase 4 : collecte de la sortie ---")
    collect_output(our_config.get("model_name", "hey_carlson"))
