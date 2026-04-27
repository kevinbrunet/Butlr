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
import wave
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
    cfg["output_dir"]    = str(WORK_DIR)   # OWW : positive_train_output_dir = output_dir/model_name/positive_train
    cfg["n_samples"]     = n_samples
    cfg["n_samples_val"] = max(100, n_samples // 10)
    cfg["steps"]         = n_epochs * 100   # ~ conversion epochs -> steps

    cfg["target_accuracy"] = 0.98  # stopping criterion : ~2% FP rate sur le validation set
    cfg["target_recall"]   = 0.7   # stopping criterion : recall >= 70% sur le test set

    # Données pré-calculées (pas de téléchargement audioset/FMA)
    cfg["feature_data_files"] = {
        "ACAV100M_sample": str(FEATURES_DIR / "openwakeword_features_ACAV100M_2000_hrs_16bit.npy")
    }
    cfg["false_positive_validation_data_path"] = str(
        FEATURES_DIR / "validation_set_features.npy"
    )
    cfg["background_paths"] = []
    cfg["rir_paths"] = []

    # Clé plate lue par OWW train.py : config["piper_sample_generator_path"]
    # (pas une nested dict — cf. exemples/custom_model.yml du repo OWW)
    cfg["piper_sample_generator_path"] = str(PIPER_GEN)
    # Modèle speaker utilisé par le fork dscripka
    cfg["speaker_model_path"] = str(PIPER_GEN / "models" / "en-us-libritts-high.pt")

    # Clips réels enregistrés par l'utilisateur — fortement recommandés pour
    # combler le gap entre voix TTS synthétique et voix humaine réelle.
    custom_dir = our.get("custom_positive_clips_dir", "")
    if custom_dir:
        custom_path = DATA_DIR / custom_dir
        if custom_path.exists() and any(custom_path.glob("*.wav")):
            cfg["custom_model_data"] = str(custom_path)
            n_clips = len(list(custom_path.glob("*.wav")))
            log.info("Clips reels trouves : %d fichiers dans %s", n_clips, custom_path)
        else:
            log.warning(
                "custom_positive_clips_dir=%s specifie mais dossier absent ou vide — "
                "enregistre des clips avec scripts/record_wakeword.py",
                custom_path,
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
      Utilise wget -c pour reprendre automatiquement en cas de coupure.
    """
    # Tailles minimales attendues — un fichier plus petit = téléchargement tronqué.
    MIN_SIZES_MB = {
        "openwakeword_features_ACAV100M_2000_hrs_16bit.npy": 3_000,  # ~ 4-6 GB
        "validation_set_features.npy": 100,
    }

    FEATURES_DIR.mkdir(parents=True, exist_ok=True)
    for filename, url in FEATURES_URLS.items():
        dest = FEATURES_DIR / filename
        min_mb = MIN_SIZES_MB.get(filename, 1)
        if dest.exists():
            size_mb = dest.stat().st_size // (1024 * 1024)
            if size_mb >= min_mb:
                log.info("Features en cache : %s (%d MB)", filename, size_mb)
                continue
            log.warning(
                "Fichier tronque detecte : %s (%d MB < %d MB min) — re-telechargement.",
                filename, size_mb, min_mb,
            )
            dest.unlink()
        log.info("Telechargement %s ...", filename)
        log.info("  (peut prendre plusieurs minutes — wget -c reprend si coupure)")
        # wget -c : resume si fichier partiel ; --tries=5 : 5 tentatives auto.
        result = subprocess.run(
            ["wget", "-c", "--tries=5", "--timeout=60", "-O", str(dest), url],
            check=False,
        )
        if result.returncode != 0:
            log.error("Telechargement echoue pour %s (exit %d)", filename, result.returncode)
            sys.exit(result.returncode)
        size_mb = dest.stat().st_size // (1024 * 1024)
        log.info("  -> %s (%d MB)", dest, size_mb)


def find_generate_samples() -> Path:
    """Localise generate_samples.py dans piper-sample-generator (structure variable selon version)."""
    if not PIPER_GEN.exists():
        log.error("Répertoire piper-gen absent : %s", PIPER_GEN)
        sys.exit(1)
    candidates = list(PIPER_GEN.rglob("generate_samples.py"))
    if not candidates:
        log.error(
            "generate_samples.py introuvable sous %s\nContenu : %s",
            PIPER_GEN,
            [p.name for p in PIPER_GEN.iterdir()],
        )
        sys.exit(1)
    found = candidates[0]
    log.info("generate_samples.py trouvé : %s", found)
    return found.parent


def verify_piper_gen() -> Path:
    """Retourne le dossier parent de generate_samples.py à ajouter au PYTHONPATH."""
    gs_dir = find_generate_samples()
    result = subprocess.run(
        [sys.executable, "-c",
         f"import sys; sys.path.insert(0, {str(gs_dir)!r}); import generate_samples; print('import OK')"],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        log.error("Import generate_samples échoué :\n%s\n%s", result.stdout, result.stderr)
        sys.exit(1)
    log.info("import generate_samples : OK (path=%s)", gs_dir)
    return gs_dir


def run_phase(label: str, flag: str, extra_pythonpath: str = "") -> None:
    """Exécute une phase du script train.py d'openWakeWord."""
    log.info("=== %s ===", label)
    env = os.environ.copy()
    parts = [p for p in [extra_pythonpath, env.get("PYTHONPATH", "")] if p]
    env["PYTHONPATH"] = ":".join(parts)
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


def inject_real_clips(our_config: dict) -> None:
    """Copy real voice recordings into OWW positive_train/ after --generate_clips.

    OWW --generate_clips only creates TTS (libritts) synthetic clips. Injecting real
    recordings here before --augment_clips ensures the model learns the actual user
    voice, closing the domain gap between synthetic and real speech.

    Expected WAV format: 16 kHz, mono, 16-bit — same as record_wakeword.py output.
    80% of recordings go to train, 20% to test (sorted alphabetically for stability).
    """
    custom_dir = our_config.get("custom_positive_clips_dir", "")
    if not custom_dir:
        log.info("custom_positive_clips_dir non configure — uniquement clips synthetiques.")
        return

    recordings_path = DATA_DIR / custom_dir
    wav_files = sorted(recordings_path.glob("*.wav"))
    if not wav_files:
        log.warning("Dossier de clips reels vide : %s — injection ignoree.", recordings_path)
        return

    model_name = our_config.get("model_name", "hey_carlson")
    clips_train_dir = WORK_DIR / model_name / "positive_train"
    clips_test_dir  = WORK_DIR / model_name / "positive_test"

    if not clips_train_dir.exists():
        log.error(
            "positive_train introuvable : %s\n"
            "OWW --generate_clips n'a pas cree ce dossier. Verifier les logs Phase 1.",
            clips_train_dir,
        )
        sys.exit(1)

    clips_test_dir.mkdir(parents=True, exist_ok=True)

    n_test = max(1, len(wav_files) // 5)
    train_files = wav_files[n_test:]
    test_files  = wav_files[:n_test]

    for src in train_files:
        shutil.copy2(src, clips_train_dir / f"real_{src.name}")
    for src in test_files:
        shutil.copy2(src, clips_test_dir / f"real_{src.name}")

    log.info(
        "Clips reels injectes dans positive_clips/ : %d train, %d test (source : %s)",
        len(train_files), len(test_files), recordings_path,
    )


def ensure_negative_clips(model_name: str, n_train: int = 10, n_test: int = 3) -> None:
    """Crée des clips de bruit blanc dans negative_train/ et negative_test/.

    OWW --augment_clips appelle next() sur le générateur de négatifs sans vérifier
    que n_total > 0 (bug OWW). Avec background_paths=[], le dossier serait vide et
    StopIteration ferait crasher la phase 2. Ces clips bruit ne servent qu'à débloquer
    le pipeline — ACAV100M reste la vraie source de négatifs pour le training.
    """
    import numpy as np

    model_dir = WORK_DIR / model_name

    for subdir, count in (("negative_train", n_train), ("negative_test", n_test)):
        clip_dir = model_dir / subdir
        clip_dir.mkdir(parents=True, exist_ok=True)
        existing = len(list(clip_dir.glob("noise_*.wav")))
        if existing >= count:
            continue
        for i in range(existing, count):
            # 2 s de bruit blanc faible amplitude (5% de la pleine échelle) 16kHz mono 16-bit
            samples = (np.random.uniform(-0.05, 0.05, 16000 * 2) * 32767).astype(np.int16)
            with wave.open(str(clip_dir / f"noise_{i:03d}.wav"), "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(2)
                wf.setframerate(16000)
                wf.writeframes(samples.tobytes())
        log.info("Clips negatifs (bruit) crees dans %s : %d", clip_dir, count)


def collect_output(model_name: str) -> None:
    """Copie .onnx et .tflite depuis <model_name>/ vers /data/."""
    src_dir = WORK_DIR / model_name
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

    skip_synthetic = our_config.get("skip_synthetic_generation", False)
    piper_pythonpath = ""

    if skip_synthetic:
        custom_dir = our_config.get("custom_positive_clips_dir", "")
        n_real = len(list((DATA_DIR / custom_dir).glob("*.wav"))) if custom_dir else 0
        if n_real == 0:
            log.error(
                "skip_synthetic_generation=true mais aucun clip dans custom_positive_clips_dir "
                "(%s). Lance scripts/Generate-FrenchTTS.ps1 d'abord.",
                DATA_DIR / custom_dir if custom_dir else "(non configure)",
            )
            sys.exit(1)
        log.info(
            "--- Phase 1 : ignoree (skip_synthetic_generation=true, %d clips disponibles) ---",
            n_real,
        )
        model_dir = WORK_DIR / our_config.get("model_name", "hey_carlson")
        for subdir in ("positive_train", "positive_test", "negative_train", "negative_test"):
            (model_dir / subdir).mkdir(parents=True, exist_ok=True)
    else:
        log.info("--- Phase 1 : generation des clips positifs (piper-sample-generator) ---")
        piper_pythonpath = str(verify_piper_gen())
        run_phase("generate_clips", "--generate_clips", extra_pythonpath=piper_pythonpath)

    log.info("--- Phase 1.5 : injection des enregistrements reels dans positive_clips/ ---")
    inject_real_clips(our_config)
    ensure_negative_clips(our_config.get("model_name", "hey_carlson"))

    import subprocess as _sub
    _grep = _sub.run(
        ["grep", "-n", "output_dir", str(OWW_TRAIN_SCRIPT)],
        capture_output=True, text=True,
    )
    log.info("--- OWW output_dir references ---\n%s", _grep.stdout[:2000])

    log.info("--- Phase 2 : augmentation des clips ---")
    run_phase("augment_clips", "--augment_clips", extra_pythonpath=piper_pythonpath)

    log.info("--- Phase 3 : entrainement du modele ---")
    run_phase("train_model", "--train_model", extra_pythonpath=piper_pythonpath)

    log.info("--- Phase 4 : collecte de la sortie ---")
    collect_output(our_config.get("model_name", "hey_carlson"))
