# ADR 0036 — TTS : Pocket TTS avec voix clonées pré-exportées en .safetensors

## Status

Accepted

## Context

Chaque locuteur détecté doit être restitué en français avec une voix synthétique distincte (clonage vocal), en streaming, avec un budget time-to-first-chunk p95 < 400ms (cf. budget de latence). Le hardware cible pour le POC est une RTX 5090, mais toutes les métriques doivent permettre un dimensionnement futur sur Raspberry Pi 5 + AI HAT+2 (hors scope POC mais contrainte de collecte de données, cf. T4.4).

Pocket TTS (Kyutai) supporte le clonage vocal à partir de ~5s d'audio, le streaming de synthèse, et tourne CPU-only.

~ Les variantes 24 couches (`french_24l`) sont annoncées plus lentes que les variantes 6 couches (`french`) — à confirmer par benchmark (T2.1).

✓ Le traitement d'un wav brut pour en extraire un embedding vocal est lent ; le chargement d'un embedding déjà exporté (.safetensors) est très rapide — d'où la nécessité de pré-exporter les voix hors du chemin critique (cf. règle transverse "modèles chauds").

## Decision

Pocket TTS est le moteur TTS retenu. Les voix (par locuteur) sont exportées en `.safetensors` via `export-voice` en tâche de fond, jamais traitées à la volée sur le chemin critique de synthèse. Le choix entre le modèle `french_24l` et `french` (6 couches) est arbitré par benchmark (T2.1, gate TTFC p95 < 400ms), pas fixé a priori.

## Consequences

- Le chemin critique de synthèse ne fait jamais de traitement audio brut (wav → embedding) — seulement du chargement d'embedding pré-exporté, cohérent avec la règle transverse "modèles chauds, embeddings vocaux en .safetensors, jamais de traitement wav sur le chemin critique".
- Avant qu'un embedding par locuteur soit disponible (les premières ~8-10s de parole d'un nouveau locuteur), il faut une voix FR générique de repli (cf. Phase 3, T3.1) — complexité additionnelle de bascule à chaud sans re-synthèse du passé (T3.2).
- Si `french_24l` dépasse le budget TTFC, on prend `french` (6l) par défaut et on documente l'écart de qualité perçu plutôt que de renégocier le budget.
- ⚠ Le clonage cross-lingue (échantillon vocal source EN/ZH → synthèse FR) n'est pas documenté explicitement par Kyutai — c'est le point le plus incertain du POC (T3.3). Le repli si échec est un pool de voix FR pré-clonées assignées par `speaker_id` (identité vocale non préservée, juste distincte par locuteur).

## Alternatives considérées

- **Coqui TTS / XTTS-v2** : rejeté — pas retenu au design initial, pas de benchmark comparatif prévu au POC ; Pocket TTS est déjà validé CPU-only streaming avec export d'embeddings rapide, ce qui correspond directement à la contrainte de dimensionnement futur Raspberry Pi.
- **TTS cloud avec clonage vocal (ex. ElevenLabs)** : rejeté sans évaluation — violerait le principe local-first (cf. `CLAUDE.md`, "Ce qu'il NE faut PAS faire").
- **Une seule voix FR partagée pour tous les locuteurs (pas de clonage)** : rejeté comme solution par défaut — mais reste le repli documenté si T3.3 échoue (pool de voix pré-clonées, pas une voix unique).

## Révisions

- 2026-07-07 — création
- 2026-07-07 — clarification suite à [ADR 0039](0039-single-process-embedded-libraries-no-websocket.md) : Pocket TTS est utilisé en bibliothèque embarquée dans le process de l'orchestrateur (`TTSModel.load_model()`, `generate_audio_stream()`, `import_model_state()`/`export_model_state()` — cf. `pypi.org/project/pocket-tts`), pas via un serveur séparé. Ne change pas la décision de cet ADR (moteur TTS + `.safetensors`), seulement le mode d'intégration — cohérent avec ce que "CPU-only" et "streaming" impliquaient déjà ici.
- 2026-07-15 — correction suite à lecture de la doc officielle (kyutai-labs.github.io/pocket-tts, github.com/kyutai-labs/pocket-tts) pour câbler `loom_orchestrator/tts_pocket.py` : ✓ le modèle `french` par défaut est annoncé 12 couches (pas 6 comme supposé initialement en Context ci-dessus), `french_24l` reste la variante plus grande/plus lente — corrige l'hypothèse "~" non vérifiée. Un premier composant (`tts_pocket.PocketTtsSynthesizer`) utilise une voix de repli unique (`estelle`, un des noms courts documentés) plutôt qu'un clonage par locuteur — T3.1-T3.3 (export de voix par locuteur) toujours pas commencés, ce composant n'est qu'un point de départ pour valider le câblage bout-en-bout (`bench/harness_pipeline.py`), pas une implémentation de la décision de cet ADR sur le clonage.
