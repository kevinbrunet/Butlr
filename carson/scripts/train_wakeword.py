"""Utilitaire de configuration pour l'entraînement du wake word "Hey Carson".

Ce script gère la config YAML et peut lancer l'entraînement nanowakeword directement.
L'entraînement passe normalement par Docker pour isoler les deps lourdes (torch, etc.) :

    ./scripts/train-wakeword.sh                # entraînement CPU via Docker
    ./scripts/train-wakeword.sh --gpu          # entraînement GPU

Usage :
    python carson/scripts/train_wakeword.py                   # affiche les instructions
    python carson/scripts/train_wakeword.py --generate-config # écrit le YAML de config
    python carson/scripts/train_wakeword.py --run             # lance si nanowakeword[train] installé

Sortie attendue :
    carson/assets/wakeword/hey_carson.onnx        (modèle principal)
    carson/assets/wakeword/hey_carson_lite.onnx   (modèle lite/distilled)
"""

from __future__ import annotations

import argparse
import logging
import subprocess
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(levelname)s  %(message)s")
log = logging.getLogger("train_wakeword")

_SCRIPT_DIR = Path(__file__).parent
_CARSON_DIR = _SCRIPT_DIR.parent
_ASSETS_DIR = _CARSON_DIR / "assets" / "wakeword"
_CONFIG_PATH = _ASSETS_DIR / "training_config.yaml"

# ~ Format basé sur nanowakeword>=2.0 — cf. CONFIGURATION_GUIDE.md pour la liste complète.
_TRAINING_CONFIG = """\
# nanowakeword — config d'entraînement pour "Hey Carson"
# Référence : https://github.com/arcosoph/nanowakeword/blob/main/CONFIGURATION_GUIDE.md

# Identifiant du modèle (nom des fichiers .onnx en sortie)
model_name: hey_carson

# Architecture réseau.
# lstm = bonne robustesse bruit pour phrase multi-syllabique. ~
# Alternatives : dnn (plus léger), conformer (plus précis, plus lent à entraîner).
model_type: lstm

# Phrase cible — nanowakeword génère les samples positifs synthétiques via TTS interne.
target_phrase: "Hey Carson"

# Dossier de clips réels enregistrés avec record_wakeword.py (optionnel mais recommandé).
# Comble le gap entre voix TTS et voix humaine réelle.
# custom_positive_clips_dir: "real_clips"

# ~ Nombre d'epochs d'entraînement. 100 suffit pour un premier test ;
# augmenter à 300-500 pour une meilleure précision.
n_epochs: 100

# Seuil de score pour la détection (repris dans Carson via WAKEWORD_THRESHOLD).
# Valeur plus haute = moins de faux positifs, mais plus de manqués.
detection_threshold: 0.5
"""


def write_config() -> Path:
    _ASSETS_DIR.mkdir(parents=True, exist_ok=True)
    _CONFIG_PATH.write_text(_TRAINING_CONFIG, encoding="utf-8")
    log.info("Config ecrite : %s", _CONFIG_PATH)
    return _CONFIG_PATH


def print_docker_instructions() -> None:
    lines = [
        "",
        "=" * 70,
        "  GUIDE D'ENTRAINEMENT -- Docker (recommande)",
        "=" * 70,
        "",
        "L'entrainement nanowakeword necessite torch + deps lourdes — on passe par Docker.",
        "",
        "ETAPE 1 -- Lance l'entrainement (depuis le repo racine) :",
        "",
        "  ./scripts/train-wakeword.sh          # CPU (~1-3 h)",
        "  ./scripts/train-wakeword.sh --gpu     # GPU NVIDIA (~20-40 min)",
        "",
        "  Le container va :",
        '    a) Generer les clips audio "Hey Carson" via TTS interne nanowakeword',
        "    b) Extraire les features audio",
        "    c) Entrainer le modele LSTM et le distiller",
        "    d) Produire hey_carson.onnx + hey_carson_lite.onnx",
        "",
        "ETAPE 2 -- Teste localement",
        "  export USE_WAKEWORD=1",
        "  carson",
        "  -> Dis 'Hey Carson' et verifie que Carson repond.",
        "  -> Laisse tourner 10 min en silence, compte les faux declenchements.",
        "     Objectif : <= 1 declenchement intempestif sur 10 min d'ambiant.",
        "  -> Trop de faux positifs : augmente WAKEWORD_THRESHOLD (0.6, 0.7...)",
        "  -> Manque trop souvent   : baisse  WAKEWORD_THRESHOLD (0.4, 0.3...)",
        "",
        "-" * 70,
        "  ALTERNATIVE -- Direct (si nanowakeword[train] est installe)",
        "-" * 70,
        "",
        "    pip install 'nanowakeword[train]>=2.0'",
        "    python carson/scripts/train_wakeword.py --run",
        "",
    ]
    print("\n".join(lines))


def _check_nanowakeword_train() -> bool:
    try:
        result = subprocess.run(
            [sys.executable, "-c", "from nanowakeword.trainer import train"],
            capture_output=True,
            check=False,
        )
        return result.returncode == 0
    except Exception:
        return False


def run_training(config_path: Path) -> None:
    if not _check_nanowakeword_train():
        log.error(
            "nanowakeword training extras manquants.\n"
            "  -> pip install 'nanowakeword[train]>=2.0'"
        )
        sys.exit(1)

    output_dir = _ASSETS_DIR / "trained_models"
    output_dir.mkdir(parents=True, exist_ok=True)

    log.info("Lancement nanowakeword (generate + transform + train + distill)...")
    # ~ CLI nanowakeword>=2.0 : flags -G -t -T -d
    result = subprocess.run(
        [
            sys.executable, "-m", "nanowakeword.cli",
            "-c", str(config_path),
            "-G", "-t", "-T", "-d",
        ],
        check=False,
    )
    if result.returncode != 0:
        log.error("nanowakeword training echoue (code %d)", result.returncode)
        sys.exit(1)

    import glob
    onnx_files = glob.glob(str(output_dir / "**" / "*.onnx"), recursive=True)
    if onnx_files:
        for f in onnx_files:
            log.info("Modele produit : %s", f)
        log.info("Active le wake word : USE_WAKEWORD=1 carson")
    else:
        log.error("Aucun .onnx trouve dans %s — verifier les logs.", output_dir)
        sys.exit(1)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Prépare et lance l'entraînement du wake word Hey Carson"
    )
    parser.add_argument(
        "--generate-config",
        action="store_true",
        help="écrit le fichier YAML de configuration et quitte",
    )
    parser.add_argument(
        "--run",
        action="store_true",
        help="lance l'entraînement complet (nécessite nanowakeword[train])",
    )
    args = parser.parse_args()

    if args.generate_config:
        write_config()
        print(f"\nConfig ecrite : {_CONFIG_PATH}")
        print("Tu peux l'editer avant de lancer l'entrainement.")
        return

    if args.run:
        config = _CONFIG_PATH if _CONFIG_PATH.exists() else write_config()
        run_training(config_path=config)
        return

    print_docker_instructions()


if __name__ == "__main__":
    main()
