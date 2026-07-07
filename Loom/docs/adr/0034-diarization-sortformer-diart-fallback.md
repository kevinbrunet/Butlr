# ADR 0034 — Diarisation : Sortformer en primaire, diart en repli

## Status

Accepted

## Context

WhisperLiveKit (ADR-0033) supporte plusieurs backends de diarisation interchangeables via `--diarization-backend`. Le choix du backend a un impact direct sur la stabilité des `speaker_id` dans le temps (un locuteur = un id stable), critique pour l'attribution de la bonne voix TTS par locuteur (cf. Phase 3 du backlog).

~ Sortformer est annoncé meilleur en streaming que diart dans la littérature/communauté consultée, mais ce n'est pas re-mesuré sur notre corpus.

⚠ Point d'attention opérationnel : les extras `diarization-sortformer` et `voxtral-hf` de WhisperLiveKit sont incompatibles entre eux et nécessitent des environnements Python séparés.

## Decision

Sortformer est le backend de diarisation par défaut. diart est conservé en repli explicite (`--diarization-backend diart`), activable sans changement de code si Sortformer échoue le gate de T1.4 (stabilité des `speaker_id`, retard des labels).

## Consequences

- `env-wlk` doit être construit avec l'extra `diarization-sortformer`, et non `voxtral-hf`, pour éviter le conflit de dépendances — à documenter dans T0.1 (setup des environnements).
- Bascule vers diart = changement de flag de config, pas de refactor — le repli est peu coûteux si Sortformer déçoit.
- Le retard des labels de diarisation par rapport au texte commité doit être mesuré (T1.4) : un retard trop important pénaliserait l'attribution de voix TTS par locuteur, sans pour autant bloquer le flux (cf. règle transverse "diarisation en retard = label appliqué aux groupes suivants").

## Alternatives considérées

- **pyannote.audio (diarisation hors-ligne classique)** : rejeté — pas conçu pour du streaming incrémental, nécessiterait un re-traitement par fenêtre, incompatible avec le budget de latence.
- **Diarisation maison (clustering d'embeddings speaker en ligne)** : rejeté — hors scope POC, complexité et risque disproportionnés par rapport au bénéfice, alors que deux backends streaming existent déjà dans WLK.

## Révisions

- 2026-07-07 — création
- 2026-07-15 — premier run réel T1.4 (corpus `b`, mix synthétique 2 locuteurs avec recouvrement 20s-65s, `bench/harness.py b`) : Sortformer produit bien deux `speaker_id` stables et distincts sur toute la durée du fichier — pas de dérive/fusion des identifiants. Latence largement dans le budget (wlk p95=564ms, 3/263 dépassements). ⚠ Défaut constaté (pas encore quantifié en fréquence, un seul run) : au moins une phrase du locuteur 2 ("having little or no money in my purse...", ouverture de Moby Dick) s'est retrouvée scindée et rattachée au locuteur 1 en plein milieu d'une autre phrase — cohérent avec la zone de recouvrement attendue (20-65s) et avec le risque déjà identifié dans ce ADR. Pas encore un motif de bascule vers diart (le gate porte sur la stabilité des id, pas sur l'exactitude à 100% pendant le recouvrement), mais à surveiller si ça se reproduit sur d'autres runs ou dégrade l'attribution de voix TTS (Phase 3).
