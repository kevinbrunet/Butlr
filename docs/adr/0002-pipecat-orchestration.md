# ADR 0002 — Pipecat comme framework d'orchestration audio

**Date** : 2026-04-23
**Statut** : Accepté

## Contexte

On veut un pipeline voice-to-voice local avec wake word, VAD, STT, LLM avec tool calling, TTS, gestion de l'interruption (barge-in). Écrire ça à la main est possible mais coûteux.

## Options considérées

- **Pipecat** (Daily) — framework frame-based Python, open source BSD-2 ✓, supporte nativement STT/LLM/TTS streaming et function calls. Communauté active fin 2025 ~.
- **LiveKit Agents** — solide mais conçu autour d'une infra LiveKit, pénible à faire tourner 100 % localement.
- **Custom** — on pilote soi-même les queues audio/texte. Maximum de contrôle, beaucoup de plomberie.

## Décision

Pipecat. Raisons :
1. Modèle frame-based adapté au temps réel : chaque composant est un FrameProcessor qui consomme/produit des frames typés (AudioFrame, TextFrame, FunctionCallFrame…). On peut insérer un sidecar (cf. filler) proprement.
2. Intégrations existantes pour Whisper, vLLM/OpenAI-compatible, Piper — on code juste la colle.
3. Licence permissive, exécutable entièrement en local.

## Conséquences

- Dépendance à un framework en évolution rapide. ~ On pin les versions, on suit les release notes.
- Le design des filler sidecars suppose qu'on comprenne finement l'ordre des frames (cf. §6.4 de l'architecture). Premier vrai test à faire dès la slice 3.
- Si Pipecat évolue de manière bloquante, migration vers custom envisageable — mais pas vers LiveKit (contrainte "local uniquement").
