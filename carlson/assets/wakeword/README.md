# Wake word assets

Placer ici le modèle `hey_carlson.tflite` une fois entraîné.

Procédure d'entraînement (à détailler dans `docs/wake-word-training.md`) :
1. Installer openWakeWord avec les deps d'entraînement.
2. Générer les données positives via Piper TTS (plusieurs voix FR, bruit de fond simulé).
3. Lancer l'entraînement (~ 30 min à 2 h selon la machine).
4. Évaluer le taux de fausses activations sur un enregistrement de conversation quotidienne.
5. Déposer le `.tflite` ici.

Seuil conseillé au démarrage : 0.5. Ajuster après mesure.
