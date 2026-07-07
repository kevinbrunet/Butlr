# CLAUDE.md — Loom

Sous-projet de [Butlr](../CLAUDE.md) — dont il hérite toutes les conventions (marqueurs de confiance, règles ADR, style de code). Ce fichier documente ce qui est spécifique à Loom. Les règles transverses au dépôt (`.claude/rules/`) s'appliquent ici sans exception.

## Projet

Loom = POC d'interprétariat simultané EN/ZH → FR. Audio d'un intervenant (anglais ou chinois) en entrée → audio français en sortie, une voix synthétique clonée par intervenant. Cible latence bout-en-bout p95 : 1,5-2s. Hardware de dev : RTX 5090 / Fedora. Cible future de dimensionnement (hors POC) : Raspberry Pi 5 + AI HAT+2.

**Stade actuel : T1.4 validé, premier câblage bout-en-bout en cours (préliminaire à T2.3).** STT WLK confirmé fidèle sur corpus réel (2026-07-14). Traduction NLLB abandonnée (hallucinations récurrentes) au profit de SeamlessM4T v2, validé sur la machine cible (2026-07-15, repli boucles de répétition corrigé). Diarisation Sortformer/NeMo installée et validée sur un run à 2 locuteurs avec recouvrement (2026-07-15) : `speaker_id` stables, contamination de contenu résiduelle en zone de recouvrement (cf. ADR-0034). Premier harnais WLK→Seamless→Pocket TTS écrit (`bench/harness_pipeline.py`), pas encore exécuté sur la machine cible. Voir le backlog complet transmis par Kevin (Phases 0-4) pour le détail des tâches.

## Documentation de référence

- `docs/adr/` — décisions d'architecture spécifiques à Loom, numérotées dans la séquence globale du dépôt (cf. `../.claude/rules/adr-writing.md`) :
  - `0033` — WhisperLiveKit pour STT streaming + diarisation + traduction intégrées (choix moteur — *superseded partiellement par 0039 sur l'aspect serveur*)
  - `0034` — Diarisation : Sortformer en primaire, diart en repli
  - `0035` — Traduction via NLLB intégré à WLK, pas de LLM (*superseded par 0040*)
  - `0036` — TTS : Pocket TTS avec voix clonées pré-exportées en `.safetensors`
  - `0037` — Orchestration en asyncio pur, sans Pipecat (*superseded par 0039 sur la topologie process*)
  - `0039` — Process unique, bibliothèques embarquées (pas de serveur, pas de WebSocket)
  - `0040` — SeamlessM4T v2 remplace NLLB pour la traduction, en 2 phases (batch puis streaming) — **ADR de référence pour la traduction actuelle**

## Architecture

```
Micro (16 kHz mono)
   │
   ▼
┌───────────────────────────────────────────────────────────┐
│ Process unique (asyncio)                                   │
│                                                              │
│  AudioProcessor (WhisperLiveKit, bibliothèque)              │
│   — STT streaming + diarisation (WLK ne traduit plus,       │
│     cf. ADR-0040 — traduction NLLB retirée)                 │
│   — process_audio() / create_tasks() : appel direct         │
│         │  segment audio complet par tour de parole         │
│         ▼                                                    │
│  SeamlessTranslator (SeamlessM4T v2, bibliothèque)          │
│   — SeamlessM4Tv2ForSpeechToText, pas de vocoder            │
│   — Phase 1 : par tour de parole. Phase 2 (si besoin) :      │
│     SeamlessStreaming, mot-à-mot (cf. ADR-0040)              │
│         │                                                    │
│         ▼                                                    │
│  Orchestrateur (notre code) — politique de commit,          │
│  registre voix, file bornée                                 │
│         │                                                    │
│         ▼                                                    │
│  TTSModel (Pocket TTS, bibliothèque)                         │
│   — generate_audio_stream(), embeddings .safetensors         │
│   — inférence déportée en executor (asyncio.to_thread)       │
└───────────────────────────────────────────────────────────┘
```

Pas de serveur, pas de WebSocket, pas de sérialisation entre les composants (cf. ADR-0039) : WhisperLiveKit, SeamlessM4T v2 et Pocket TTS sont importés comme bibliothèques dans le même process que l'orchestrateur. On ne code que la politique de commit, le registre de voix et la glue asyncio — STT et diarisation restent délégués à WhisperLiveKit (cf. ADR-0033), la traduction à SeamlessM4T v2 (cf. ADR-0040, jamais son propre module Expressive — la sortie texte va à Pocket TTS).

## Stack

| Zone | Tech | Notes |
|---|---|---|
| STT + diarisation | WhisperLiveKit (bibliothèque, `TranscriptionEngine`/`AudioProcessor`) | Diarisation Sortformer installée et validée (T1.4, 2026-07-15 — `speaker_id` stables sur 2 locuteurs avec recouvrement, cf. ADR-0034), repli diart toujours disponible si besoin. Traduction NLLB **retirée** (ADR-0040) : `target_language` non utilisé. |
| Traduction | SeamlessM4T v2 (bibliothèque, `SeamlessM4Tv2ForSpeechToText`, `loom_orchestrator/translation_seamless.py`) | Phase 1 (ADR-0040) : par segment/tour de parole complet, pas mot-à-mot. ~5,8 Go VRAM fp16. Codes langue ISO 639-3 (`eng`/`cmn`/`fra`) via `resolve_language_code`. |
| TTS | Pocket TTS (bibliothèque, `TTSModel`, `loom_orchestrator/tts_pocket.py`) | ✓ Constaté par exécution réelle (2026-07-15) : pas de variante FR plus légère que 24 couches — `french_24l` uniquement (`TTSModel.load_model(language="french")` échoue explicitement, cf. Révisions ADR-0036). ⚠ Pas encore de clonage par locuteur : `PocketTtsSynthesizer` utilise une unique voix FR de repli (`estelle`) en attendant T3.1-T3.3 ; voix clonées prévues en `.safetensors` (`export_model_state`/`import_model_state`). |
| Orchestrateur | Python 3.12, asyncio, `uv` | **Un seul process** (cf. ADR-0039). Pas de framework de pipeline audio (cf. ADR-0037). Inférence TTS déportée en executor pour ne pas bloquer la boucle événementielle. |
| Environnement | `env-loom/` (venv unique) | Extras `diarization-sortformer` et `voxtral-hf` de WLK sont incompatibles entre eux — ne jamais les installer ensemble (cf. ADR-0034). |
| Déploiement dev | Docker Compose (1 service) | |

## Convention spécifique : mesurer avant d'optimiser

Aucun changement de config (modèle STT, backend diarisation, modèle TTS, `min_commit_words`...) sans benchmark avant/après sur le corpus de test (`corpus/`, cf. T0.2). Les budgets de latence par étage sont codés en dur dans l'instrumentation ; un dépassement est un WARNING loggué, jamais une variabilité silencieusement acceptée. Le budget "Transport" (100ms) devient une marge de sécurité plutôt qu'un coût réseau réel depuis ADR-0039 — pas de raison de le retirer du budget total sans mesure.

- `corpus/` — 4 wav 16kHz versionnés dans le repo (manifeste et provenance : `env-loom/src/loom_orchestrator/bench/corpus.py`).
- `env-loom/src/loom_orchestrator/bench/` — outillage de benchmark (T0.2-T0.4) : `replay.py` (injection temps réel, `send` générique — branché sur `AudioProcessor.process_audio`), `timestamps.py`/`clock.py`/`instrumentation.py` (log JSON lines par étage), `aggregate.py` (p50/p95), `line_tracking.py` (diff de texte par ligne WLK — `lines` n'est pas append-only, cf. ADR-0039), `harness.py` (commande WLK seul — `python -m loom_orchestrator.bench.harness <clé_corpus>`), `audio_chunks.py` (`iter_duration_chunks` : segments de N secondes pour `harness_seamless.py` ; `read_segment` : fenêtre arbitraire `[start_s, end_s)` pour extraire l'audio source d'un tour de parole, utilisé par `harness_pipeline.py`), `harness_seamless.py` (commande Seamless seul, bypass WLK/NLLB — `python -m loom_orchestrator.bench.harness_seamless <clé_corpus>`), `harness_pipeline.py` (premier câblage bout-en-bout WLK→Seamless→Pocket TTS, chaque ligne WLK traduite/synthétisée une fois le flux terminé — `python -m loom_orchestrator.bench.harness_pipeline <clé_corpus>` ; écrit un wav FR par tour dans `<run>-audio/` en plus du transcript et du log de latences), `harness_tts.py` (sonde de latence Pocket TTS isolée, sans WLK ni Seamless, sur des phrases FR de longueurs variées répétées plusieurs fois — `python -m loom_orchestrator.bench.harness_tts` ; née du run T2.3-préliminaire du 2026-07-15 où le TTS mesurait p50=2993ms/p95=4340ms contre un budget de 400ms, pour savoir si c'est un coût fixe systématique ou un artefact du run bugué). ⚠ `harness.py` ne mesure que l'étage WLK (STT) ; `harness_seamless.py` ne mesure que la traduction en isolation (chunks fixes via `--chunk-s`, pas de vrais tours de parole — utile pour un balayage de durée : `--chunk-s 2`, `5`, `10`...) ; `harness_pipeline.py` est le premier assemblage réel mais reste un harnais de validation séquentiel (pas de file bornée, pas de registre de voix — c'est le travail de l'orchestrateur final, T2.3, `main.py` reste `NotImplementedError`) ; `harness_tts.py` isole une seule inconnue (coût de `generate_audio` en fonction de la longueur du texte). Tous importent des bibliothèques GPU/CPU lourdes (`whisperlivekit`, `transformers`, `pocket_tts`) : non exécutables/testables hors de la machine cible.
- `loom_orchestrator/translation_seamless.py` — `SeamlessTranslator`, hors du dossier `bench/` : c'est un composant du pipeline (pas un outil de mesure), appelé par `harness_pipeline.py` et, plus tard, par l'orchestrateur final.
- `loom_orchestrator/tts_pocket.py` — `PocketTtsSynthesizer`, même statut que `translation_seamless.py` : composant du pipeline, pas un outil de mesure. ✓ `language="french_24l"` confirmé par exécution réelle (pas d'alternative FR plus légère, cf. Révisions ADR-0036). ⚠ Le nom court de voix `"estelle"` reste vérifié par documentation seulement, pas par exécution ; pas de clonage par locuteur (voix de repli unique).

## Ce qu'il NE faut PAS faire (spécifique à Loom, en plus des règles Butlr)

- ⚠ Ne pas traiter un fichier wav brut sur le chemin critique de synthèse TTS — uniquement des embeddings `.safetensors` pré-exportés (cf. ADR-0036).
- ⚠ Ne pas laisser une file interne (WLK→orchestrateur, orchestrateur→TTS) sans taille max et politique de drop documentée dans le code.
- ⚠ Ne pas ré-émettre ou corriger de l'audio déjà joué — le passé est immuable, les révisions STT ne s'appliquent qu'au futur.
- ⚠ Ne jamais faire remonter de backpressure vers WLK (diarisation en retard = label appliqué aux groupes suivants ; TTS en retard = dégradation contrôlée, jamais de blocage amont).
- ⚠ Ne pas installer les extras `diarization-sortformer` et `voxtral-hf` de WhisperLiveKit dans le même environnement (cf. ADR-0034).
- ⚠ Ne pas lancer WhisperLiveKit ou Pocket TTS en mode serveur (CLI `whisperlivekit-server`, `pocket-tts serve` ou équivalent) — usage bibliothèque uniquement (cf. ADR-0039). Si un serveur redevient nécessaire un jour (ex. accès distant), ça se rediscute en ADR, pas en silence.
- ⚠ Ne pas laisser l'inférence Pocket TTS (bloquante, CPU) tourner directement dans la boucle asyncio principale — la déporter en executor pour ne pas geler le traitement WLK en cours (cf. ADR-0039).
- ⚠ Ne pas utiliser le module Expressive/vocoder de Seamless (synthèse vocale) — seule la sortie texte de `SeamlessM4Tv2ForSpeechToText` est utilisée, Pocket TTS reste seul responsable de la synthèse et du clonage de voix par locuteur (cf. ADR-0040).
- ⚠ Ne pas réintroduire NLLB/`nllw` (le sous-package de traduction de WLK) — confirmé peu fiable en streaming (ADR-0040). `target_language` de `TranscriptionEngine` ne doit plus être configuré.
