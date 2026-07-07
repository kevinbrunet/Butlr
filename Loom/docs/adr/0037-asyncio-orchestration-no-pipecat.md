# ADR 0037 — Orchestration en asyncio pur, sans Pipecat

## Status

Superseded by [ADR 0039](0039-single-process-embedded-libraries-no-websocket.md) — le choix "asyncio pur, pas de Pipecat" reste valable, seule l'architecture à deux process reliés par WebSocket est remplacée par un process unique avec bibliothèques embarquées.

## Context

Butlr a déjà adopté Pipecat comme framework d'orchestration de pipeline audio pour carson ([ADR 0002](../../../docs/adr/0002-pipecat-orchestration.md) — Pipecat comme framework d'orchestration audio, périmètre : wake word → VAD → STT → LLM → TTS). Pour Loom, le pipeline STT/diarisation/traduction est entièrement délégué à WhisperLiveKit ([ADR 0033](0033-whisperlivekit-stt-diarization-translation.md)), qui expose déjà un WebSocket avec sortie incrémentale structurée. Il ne reste côté Loom qu'un consommateur de ce flux, une politique de commit vers le TTS, et un registre de voix par locuteur.

## Decision

L'orchestrateur Loom est écrit en asyncio pur (Python), sans framework de pipeline audio. Architecture à deux process : WLK (STT+diarisation+traduction) et TTS+orchestrateur (consommateur WebSocket, politique de commit, registre de voix, serveur Pocket TTS).

## Consequences

- Pas de dépendance à Pipecat pour Loom, contrairement à carson — divergence assumée entre les deux sous-projets Butlr, chacun sur son propre contexte (carson gère un pipeline audio temps réel complet en local ; Loom consomme un flux déjà structuré par un serveur tiers).
- Moins d'abstraction à maintenir pour un besoin simple (un seul consommateur WebSocket) — cohérent avec le principe directeur du POC : "on ne code que l'orchestrateur TTS et la politique de commit".
- Toute évolution qui ajouterait des étages de pipeline supplémentaires côté Loom (ex. VAD custom, filler audio type [ADR 0004](../../../docs/adr/0004-filler-sidecar-pattern.md)) devrait re-questionner ce choix.
- Cette ADR ne supersede pas l'ADR-0002 : elle documente un choix distinct pour un sous-projet au périmètre différent (consommateur d'un flux déjà structuré, pas un pipeline audio local complet), pas un changement de trajectoire sur la décision existante pour carson.

## Alternatives considérées

- **Pipecat, par cohérence avec carson (ADR-0002)** : rejeté — Pipecat orchestre des pipelines audio multi-étages (VAD, STT, LLM, TTS) ; ici il n'y a qu'un seul consommateur d'un flux déjà structuré (WebSocket JSON incrémental de WLK), un framework de pipeline n'apporte plus rien et ajouterait de la complexité d'intégration sans bénéfice.
- **Un framework de queue/stream dédié (ex. Faust, RxPY)** : rejeté — sur-ingénierie pour deux process asyncio communiquant par WebSocket et une file bornée en mémoire.

## Révisions

- 2026-07-07 — création
- 2026-07-07 — superseded par ADR 0039 : process unique au lieu de deux process reliés par WebSocket.
