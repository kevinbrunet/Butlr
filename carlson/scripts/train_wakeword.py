"""Utilitaire de configuration pour l'entraînement du wake word "Hey Carlson".

Ce script gère la config YAML et, sous Linux/WSL, peut lancer l'entraînement
openWakeWord directement. Sur Windows, l'entraînement passe par Docker :

    .\\scripts\\Train-WakeWord.ps1         # entraînement CPU via Docker
    .\\scripts\\Train-WakeWord.ps1 -Gpu    # entraînement GPU

Usage :
    python carlson/scripts/train_wakeword.py                   # affiche les instructions
    python carlson/scripts/train_wakeword.py --generate-config # écrit le YAML de config
    python carlson/scripts/train_wakeword.py --run             # lance si sous Linux/WSL/Docker

Sortie attendue :
    carlson/assets/wakeword/hey_carlson.tflite   (modèle final)
    carlson/assets/wakeword/hey_carlson.onnx     (export alternatif)
"""

from __future__ import annotations

import argparse
import logging
import platform
import subprocess
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(levelname)s  %(message)s")
log = logging.getLogger("train_wakeword")

_SCRIPT_DIR = Path(__file__).parent
_CARLSON_DIR = _SCRIPT_DIR.parent
_ASSETS_DIR = _CARLSON_DIR / "assets" / "wakeword"
_CONFIG_PATH = _ASSETS_DIR / "training_config.yaml"

# ─── YAML de configuration openWakeWord ────────────────────────────────────────
# ~ Format basé sur openWakeWord>=0.6 automatic_model_training.ipynb.
#   Paramètres à ajuster selon tes ressources et tes tests de FP rate.
_TRAINING_CONFIG = """\
# openWakeWord — config d'entraînement pour "Hey Carlson"
# Référence : https://github.com/dscripka/openWakeWord/blob/main/notebooks/automatic_model_training.ipynb

# Identifiant du modèle (nom du fichier .tflite et .onnx en sortie)
model_name: hey_carlson

# Phrase cible — openWakeWord génère les samples positifs à partir de cette phrase
# via TTS multi-voix (nécessite Linux + Piper installé dans l'env openWakeWord).
target_phrase: "Hey Carlson"

# ~ Nombre de samples positifs synthétiques à générer.
# 5000 est un bon point de départ ; augmenter si FP rate trop élevé.
n_positive_samples: 5000

# ~ Nombre d'epochs d'entraînement. 100 suffit pour un premier test ;
# augmenter à 300-500 pour une meilleure précision.
n_epochs: 100

# Seuil de score pour la détection (repris dans Carlson via WAKEWORD_THRESHOLD).
# Valeur plus haute = moins de faux positifs, mais plus de manqués.
detection_threshold: 0.5

# Les négatifs (parole, musique, bruits) sont téléchargés automatiquement depuis
# Hugging Face (dataset davidscripka/openwakeword_features). Connexion internet requise.
use_precomputed_features: true
"""


def write_config() -> Path:
    """Ecrit le fichier YAML de config et retourne son chemin."""
    _ASSETS_DIR.mkdir(parents=True, exist_ok=True)
    _CONFIG_PATH.write_text(_TRAINING_CONFIG, encoding="utf-8")
    log.info("Config ecrite : %s", _CONFIG_PATH)
    return _CONFIG_PATH


def print_colab_instructions() -> None:
    """Affiche le guide pas à pas pour entraîner sur Google Colab."""
    lines = [
        "",
        "=" * 70,
        "  GUIDE D'ENTRAINEMENT -- Docker (recommande)",
        "=" * 70,
        "",
        "L'entrainement openWakeWord necessite Linux — on passe par Docker.",
        "",
        "ETAPE 1 -- Lance l'entrainement via Docker (depuis le repo racine) :",
        "",
        "  .\\scripts\\Train-WakeWord.ps1          # CPU (~2-4 h)",
        "  .\\scripts\\Train-WakeWord.ps1 -Gpu     # GPU NVIDIA (~45 min)",
        "",
        "  Le container va :",
        '    a) Generer ~5 000 clips audio "Hey Carlson" via Piper TTS',
        "    b) Telecharger les donnees negatives depuis Hugging Face",
        "    c) Entrainer le modele et produire hey_carlson.tflite",
        "",
        "  Premier lancement : ~5-10 min de build Docker + telechargement (~4-6 GB).",
        "  Les lancements suivants reutilisent l'image en cache.",
        "",
        "ETAPE 2 -- Teste localement",
        "  $env:USE_WAKEWORD = '1'",
        "  carlson",
        "  -> Dis 'Hey Carlson' et verifie que Carlson repond.",
        "  -> Laisse tourner 10 min en silence, compte les faux declenchements.",
        "     Objectif : <= 1 declenchement intempestif sur 10 min d'ambiant.",
        "  -> Trop de faux positifs : augmente WAKEWORD_THRESHOLD (0.6, 0.7...)",
        "  -> Manque trop souvent   : baisse  WAKEWORD_THRESHOLD (0.4, 0.3...)",
        "",
        "-" * 70,
        "  ALTERNATIVE -- Depuis un terminal Linux/WSL2",
        "-" * 70,
        "",
        "    pip install openwakeword[full] piper-tts",
        "    python carlson/scripts/train_wakeword.py --run",
        "",
    ]
    print("\n".join(lines))


def _check_linux() -> bool:
    return platform.system() == "Linux"


def _check_openwakeword_full() -> bool:
    """Vérifie que les extras training d'openWakeWord sont disponibles."""
    try:
        result = subprocess.run(
            [sys.executable, "-c", "from openwakeword.train import auto_train"],
            capture_output=True,
            check=False,
        )
        return result.returncode == 0
    except Exception:
        return False


def run_training(config_path: Path, output_dir: Path) -> None:
    """Lance l'entraînement openWakeWord via la CLI officielle.

    ~ Commandes basées sur openwakeword>=0.6 CLI.
      Si les arguments ont changé, consulter :
      python -m openwakeword.train --help
    """
    if not _check_linux():
        log.error(
            "L'entrainement openWakeWord necessite Linux. "
            "Utilise WSL2 ou Google Colab. Voir les instructions ci-dessus."
        )
        print_colab_instructions()
        sys.exit(1)

    if not _check_openwakeword_full():
        log.error(
            "openWakeWord training extras manquants.\n"
            "  -> pip install openwakeword[full]"
        )
        sys.exit(1)

    output_dir.mkdir(parents=True, exist_ok=True)

    log.info("Phase 1/2 -- Generation des samples positifs via TTS...")
    # ~ Arguments CLI openWakeWord>=0.6 — vérifier avec `python -m openwakeword.train --help`
    result = subprocess.run(
        [
            sys.executable, "-m", "openwakeword.train",
            "--training_config", str(config_path),
            "--augment_clips",
        ],
        check=False,
    )
    if result.returncode != 0:
        log.error("Generation de donnees echouee (code %d)", result.returncode)
        sys.exit(1)

    log.info("Phase 2/2 -- Entrainement du modele...")
    result = subprocess.run(
        [
            sys.executable, "-m", "openwakeword.train",
            "--training_config", str(config_path),
            "--train_model",
            "--output_dir", str(output_dir),
        ],
        check=False,
    )
    if result.returncode != 0:
        log.error("Entrainement echoue (code %d)", result.returncode)
        sys.exit(1)

    # openWakeWord produit <model_name>.tflite et <model_name>.onnx dans output_dir
    tflite = output_dir / "hey_carlson.tflite"
    if tflite.exists():
        size_kb = tflite.stat().st_size // 1024
        log.info("Modele pret : %s (%d KB)", tflite, size_kb)
        log.info("Active le wake word : $env:USE_WAKEWORD='1'; carlson")
    else:
        log.error(
            "hey_carlson.tflite introuvable dans %s apres entrainement.\n"
            "Verifie les logs ci-dessus.",
            output_dir,
        )
        sys.exit(1)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Prépare et lance l'entraînement du wake word Hey Carlson"
    )
    parser.add_argument(
        "--generate-config",
        action="store_true",
        help="écrit le fichier YAML de configuration et quitte",
    )
    parser.add_argument(
        "--run",
        action="store_true",
        help="lance l'entraînement complet (Linux/WSL uniquement)",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=_ASSETS_DIR,
        help="dossier de sortie pour le .tflite (défaut: assets/wakeword/)",
    )
    args = parser.parse_args()

    if args.generate_config:
        write_config()
        print(f"\nConfig ecrite : {_CONFIG_PATH}")
        print("Tu peux l'editer avant de lancer l'entrainement.")
        return

    if args.run:
        config = _CONFIG_PATH if _CONFIG_PATH.exists() else write_config()
        run_training(config_path=config, output_dir=args.output_dir)
        return

    # Par défaut : afficher les instructions complètes
    print_colab_instructions()


if __name__ == "__main__":
    main()
