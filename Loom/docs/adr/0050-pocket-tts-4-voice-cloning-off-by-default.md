# ADR 0050 — Pocket TTS 4.0.1, clonage de voix désactivé par défaut, garde-fou de durée

## Status

Accepted — supersedes partiellement [ADR 0046](0046-personalized-voice-profiles.md) (le clonage par palier devient optionnel, `--voice-clone`, au lieu d'être actif par défaut) et [ADR 0048](0048-split-mono-multi-speaker-subprojects.md) sur le même point pour le mono.

## Context

Le 2026-10-26, Kevin demande de passer Loom aux dernières versions publiées. Pour Pocket TTS, ✓ 2.1.0 → 4.0.1 (PyPI, publiée le 2026-10-21). Ce qui a changé entre les deux, d'après les notes de version GitHub et la lecture du code source 4.0.1 :

- ✓ L'API utilisée par `tts_pocket.py` est inchangée : `TTSModel.load_model(language="french_24l")`, `get_state_for_audio_prompt` (tenseur `[canal, échantillons]`, chemin `.safetensors` ou nom de voix prédéfinie), `generate_audio_stream(state, texte, copy_state=...)`, `export_model_state`.
- ✓ Modèles français réentraînés (3.2.0), température 0,3 par défaut (3.3.0), réécriture des guillemets et apostrophes typographiques (3.3.0, 4.0.0).
- ✓ Streaming : toujours **texte complet en entrée** (découpé en phrases de 50 jetons au plus), audio par morceaux en sortie ; nouveau paramètre `stop` (`threading.Event`) pour interrompre une génération ; fondu d'entrée sur le premier morceau.

Mesures sur fedora3 (RTX 5090), mono, corpus `a` (185 s, anglais) :

- ✓ **Voix clonée, 4.0.1 :** le modèle `french_24l` émet rarement la fin de séquence et génère jusqu'à sa longueur maximale : 2,7 s d'audio pour « Oui, », 9,7 s pour 14 mots. En sonde isolée (7 phrases), cela touche presque toutes les phrases avec une invite de 20 ou 60 s, 1 à 2 sur 7 avec 5 ou 10 s. Dans le pipeline, 368 s d'audio français pour 185 s d'entrée, un TTS sur CPU à peine plus rapide que le temps réel (facteur 0,95), et le consommateur unique décroche (traduction à 220 s au p95).
- ✓ **Voix clonée, 2.1.0 :** pas fiable non plus, dans l'autre sens. Avec une invite de 60 s, l'audio est tronqué (0,8 s pour 12 mots). Le run de référence 2.1.0 ne tenait le temps réel que parce que la voix clonée produisait de l'audio trop court.
- ✓ **Voix `estelle`, 4.0.1 :** durées normales (~0,25 s par mot), aucune fin de séquence manquée.

## Decision

Pocket TTS passe en 4.0.1, dans mono comme dans multi. Le clonage de voix (clonage progressif en mono, profils personnalisés en multi) devient **optionnel** (`--voice-clone`). Par défaut, le mono parle avec `estelle` et le multi avec les voix de pool distinctes par locuteur. `PocketTtsSynthesizer.synthesize_stream` coupe toute génération dont l'audio dépasse un budget fonction du nombre de mots, grâce au `stop` de la 4.0.

## Consequences

- Le pipeline tient le temps réel avec la voix par défaut ; on perd la voix personnalisée tant que le clonage n'est pas corrigé.
- Le garde-fou empêche une génération sans fin de bloquer le consommateur unique. ⚠ Budget non calibré (0,8 s par mot + 1,5 s, soit environ deux fois un débit de 2,5 mots/s) ; il n'a coupé qu'une génération sur le run avec clonage, car l'excès d'audio venait surtout de phrases trop longues sans dépasser le budget.
- ⚠ Les voix de pool non françaises du multi (ADR-0046) n'ont pas été re-sondées avec la 4.0.1.
- Pistes pour réactiver le clonage, non tranchées : invite courte (5 à 10 s, conforme aux invites de 5 s de l'entraînement depuis 3.0.2), invite en français plutôt qu'en anglais, `eos_threshold` de `load_model`, retour d'expérience des mainteneurs.
- Le TTS reste sur CPU et à peine plus rapide que le temps réel (facteur 0,7 à 1,3 selon les runs) : c'est la limite structurelle mise en évidence. GPU ou `quantize=True` (int8, ~ +27 % annoncés sur x86) restent à évaluer ; Kevin a choisi de ne pas creuser maintenant.

## Alternatives considérées

### Rester en 2.1.0

Rejeté : Kevin veut des dépendances à jour, et la 2.1.0 ne clone pas mieux (troncature).

### 4.0.1 avec clonage, invite limitée à 10 s

Moins de dérapages mesurés (1 sur 7), mais pas zéro, et qualité non vérifiée à l'oreille. Gardé comme piste, pas comme défaut.

### Pocket TTS sur GPU

Pourrait rendre le TTS nettement plus rapide que le temps réel et absorber les phrases trop longues, mais revient sur ADR-0036 (CPU uniquement) et ne corrige pas la cause. Reporté.

## Révisions

- 2026-10-26 — création, après les runs mono corpus `a` (2.1.0, 4.0.1, 4.0.1 avec garde-fou) et les sondes isolées de durée par voix.
