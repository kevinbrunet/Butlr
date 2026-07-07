# CLAUDE.md — Loom

Sous-projet de [Butlr](../CLAUDE.md) — dont il hérite toutes les conventions (marqueurs de confiance, règles ADR, style de code). Ce fichier documente ce qui est spécifique à Loom. Les règles transverses au dépôt (`.claude/rules/`) s'appliquent ici sans exception.

## Projet

Loom = POC d'interprétariat simultané EN/ZH → FR. Audio d'un intervenant (anglais ou chinois) en entrée → audio français en sortie, une voix synthétique clonée par intervenant. Cible latence bout-en-bout p95 : 1,5-2s. Hardware de dev : RTX 5090 / Fedora. Cible future de dimensionnement (hors POC) : Raspberry Pi 5 + AI HAT+2.

**Stade actuel : T1.1 en cours.** STT WLK confirmé fidèle sur corpus réel (2026-07-14). Traduction NLLB abandonnée (hallucinations récurrentes) au profit de SeamlessM4T v2 — module écrit, pas encore validé sur la machine cible. Diarisation (Sortformer/NeMo) toujours pas installée. Voir le backlog complet transmis par Kevin (Phases 0-4) pour le détail des tâches.

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
| STT + diarisation | WhisperLiveKit (bibliothèque, `TranscriptionEngine`/`AudioProcessor`) | Diarisation Sortformer (repli diart, pas encore installée — T1.4). Traduction NLLB **retirée** (ADR-0040) : `target_language` non utilisé. |
| Traduction | SeamlessM4T v2 (bibliothèque, `SeamlessM4Tv2ForSpeechToText`, `loom_orchestrator/translation_seamless.py`) | Phase 1 (ADR-0040) : par segment/tour de parole complet, pas mot-à-mot. ~5,8 Go VRAM fp16. Codes langue ISO 639-3 (`eng`/`cmn`/`fra`) via `resolve_language_code`. |
| TTS | Pocket TTS (bibliothèque, `TTSModel`) | Modèle `french_24l` ou `french` (6l) selon benchmark T2.1. Voix clonées exportées en `.safetensors` (`export_model_state`/`import_model_state`). |
| Orchestrateur | Python 3.12, asyncio, `uv` | **Un seul process** (cf. ADR-0039). Pas de framework de pipeline audio (cf. ADR-0037). Inférence TTS déportée en executor pour ne pas bloquer la boucle événementielle. |
| Environnement | `env-loom/` (venv unique) | Extras `diarization-sortformer` et `voxtral-hf` de WLK sont incompatibles entre eux — ne jamais les installer ensemble (cf. ADR-0034). |
| Déploiement dev | Docker Compose (1 service) | |

## Convention spécifique : mesurer avant d'optimiser

Aucun changement de config (modèle STT, backend diarisation, modèle TTS, `min_commit_words`...) sans benchmark avant/après sur le corpus de test (`corpus/`, cf. T0.2). Les budgets de latence par étage sont codés en dur dans l'instrumentation ; un dépassement est un WARNING loggué, jamais une variabilité silencieusement acceptée. Le budget "Transport" (100ms) devient une marge de sécurité plutôt qu'un coût réseau réel depuis ADR-0039 — pas de raison de le retirer du budget total sans mesure.

- `corpus/` — 4 wav 16kHz versionnés dans le repo (manifeste et provenance : `env-loom/src/loom_orchestrator/bench/corpus.py`).
- `env-loom/src/loom_orchestrator/bench/` — outillage de benchmark (T0.2-T0.4) : `replay.py` (injection temps réel, `send` générique — branché sur `AudioProcessor.process_audio`), `timestamps.py`/`clock.py`/`instrumentation.py` (log JSON lines par étage), `aggregate.py` (p50/p95), `line_tracking.py` (diff de texte par ligne WLK — `lines` n'est pas append-only, cf. ADR-0039), `harness.py` (commande WLK seul — `python -m loom_orchestrator.bench.harness <clé_corpus>`), `audio_chunks.py` (découpe un wav en segments de N secondes, float32 normalisé — pour Seamless, pas pour le streaming WLK), `harness_seamless.py` (commande Seamless seul, bypass WLK/NLLB — `python -m loom_orchestrator.bench.harness_seamless <clé_corpus>`). ⚠ `harness.py` ne mesure que l'étage WLK (STT) ; `harness_seamless.py` ne mesure que la traduction en isolation, sans STT/diarisation/TTS réels. Les deux importent des bibliothèques GPU (`whisperlivekit`, `transformers`) : non exécutables/testables hors de la machine cible.
- `loom_orchestrator/translation_seamless.py` — `SeamlessTranslator`, hors du dossier `bench/` : c'est un composant du pipeline (pas un outil de mesure), destiné à être appelé par l'orchestrateur une fois câblé à la diarisation WLK.

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
