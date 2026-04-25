#!/bin/bash
# Entraînement openWakeWord en deux phases.
# Exécuté dans le container — ne pas lancer directement sur le host.
set -euo pipefail

CONFIG=/data/training_config.yaml

if [ ! -f "$CONFIG" ]; then
    echo "ERREUR : $CONFIG introuvable."
    echo "Génère-le d'abord : .\\scripts\\Train-WakeWord.ps1 -GenerateConfig"
    exit 1
fi

echo ""
echo "=== Phase 1/2 : génération des samples positifs via Piper TTS ==="
echo "    Config : $CONFIG"
echo ""
# ~ Arguments CLI openWakeWord>=0.6 — vérifier avec : python -m openwakeword.train --help
python -m openwakeword.train \
    --training_config "$CONFIG" \
    --augment_clips

echo ""
echo "=== Phase 2/2 : entraînement du modèle ==="
echo ""
python -m openwakeword.train \
    --training_config "$CONFIG" \
    --train_model \
    --output_dir /data

echo ""
echo "=== Terminé ==="
if ls /data/*.tflite 1>/dev/null 2>&1; then
    echo "Modèle(s) produit(s) :"
    ls -lh /data/*.tflite
    echo ""
    echo "Place le fichier dans carlson/assets/wakeword/ et active :"
    echo "  \$env:USE_WAKEWORD = '1'"
    echo "  carlson"
else
    echo "AVERTISSEMENT : aucun .tflite trouvé dans /data/"
    echo "Vérifie les logs ci-dessus."
    exit 1
fi
