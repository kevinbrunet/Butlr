# ADR 0039 — Process unique, bibliothèques embarquées (pas de serveur WLK/TTS, pas de WebSocket)

## Status

Accepted — supersedes [ADR 0033](0033-whisperlivekit-stt-diarization-translation.md), [ADR 0037](0037-asyncio-orchestration-no-pipecat.md)

## Context

ADR-0033 et ADR-0037 supposaient que WhisperLiveKit (WLK) et Pocket TTS tournaient chacun comme un serveur séparé (processus + WebSocket/HTTP), l'orchestrateur consommant ces deux services réseau. Cette hypothèse venait de la spec initiale ("WhisperLiveKit (serveur, WebSocket)", "Pocket TTS serveur ... mode serve") et de l'exemple `basic_server.py` des deux projets, qui illustre effectivement un usage serveur — mais qui n'est qu'un exemple d'intégration parmi d'autres, pas une contrainte de la lib.

Sur un budget de latence bout-en-bout de 1600ms p95 dont 100ms explicitement alloués à "Transport + lecture audio" (cf. budget de latence, Loom/CLAUDE.md), toute couche réseau — même en loopback — est un coût pur sans bénéfice fonctionnel ici : il n'y a ni client distant, ni besoin d'exposer une API HTTP/WS à l'extérieur du process.

✓ Vérifié par **lecture directe du code source** (`whisperlivekit/audio_processor.py`, `whisperlivekit/timed_objects.py`, `whisperlivekit/core.py`, `whisperlivekit/config.py`, `whisperlivekit/__init__.py` — repo `QuentinFuxa/WhisperLiveKit`, branche `main`, lu le 2026-07-07, pas un résumé de doc) :

- Import public : `from whisperlivekit import AudioProcessor, TranscriptionEngine`.
- `TranscriptionEngine(**kwargs)` construit un `WhisperLiveKitConfig` (dataclass) à partir des kwargs (`.from_kwargs`) — les noms de champs pertinents pour Loom existent tels quels : `pcm_input`, `diarization`, `diarization_backend` (défaut déjà `"sortformer"`, cohérent avec ADR-0034), `lan` (défaut déjà `"auto"`), `target_language`, `translation_backend` (défaut déjà `"nllb"`, cohérent avec ADR-0035), `backend_policy` (défaut déjà `"simulstreaming"`, cohérent avec ADR-0033), `model_size`.
- **`TranscriptionEngine` est un singleton process-wide** (double-checked locking dans `core.py`) : un deuxième appel avec des kwargs différents renvoie la même instance déjà initialisée, sans appliquer les nouveaux kwargs. Une grille de bench qui change de config (T1.2) doit appeler `TranscriptionEngine.reset()` entre deux runs.
- **`pcm_input=True` est obligatoire** pour notre cas d'usage : par défaut (`pcm_input=False`), `AudioProcessor` route l'audio entrant vers un process FFmpeg externe (pensé pour de l'audio compressé façon navigateur, webm/opus). Avec `pcm_input=True`, `process_audio(bytes)` traite le PCM brut directement — sample rate 16000Hz et 1 canal sont alors **hardcodés** côté `AudioProcessor` (`self.sample_rate = 16000`, `self.channels = 1`), et la largeur d'échantillon attendue est de 2 octets (PCM 16 bits, `bytes_per_sample = 2`).
- `AudioProcessor.process_audio(bytes)` alimente l'audio, `b""` signale la fin de flux. `await AudioProcessor.create_tasks()` retourne le générateur async `results_formatter()`.
- Schéma réel de `response.to_dict()` (classe `FrontData`, `whisperlivekit/timed_objects.py`) : `{"status": str, "lines": [...], "buffer_transcription": str, "buffer_diarization": str, "buffer_translation": str, "remaining_time_transcription": float, "remaining_time_transcription_processing": float, "remaining_time_transcription_policy": float, "remaining_time_diarization": float, "error"?: str}`. Chaque ligne (classe `Segment.to_dict()`) : `{"speaker": int, "text": str, "start": "H:MM:SS.cc", "end": "H:MM:SS.cc", "translation"?: str, "detected_language"?: str}` — `start`/`end` au **centième de seconde** (`format_time()`), pas à la seconde entière comme supposé dans une version précédente de cette ADR.
- **`lines` est cumulatif, pas un delta** : `results_formatter()` ne pousse un message que quand l'état a changé (`response != self.last_response_content`), et `AudioProcessor` a un paramètre `mode` (défaut `"full"`) qui renvoie tout l'historique des lignes à chaque poll — confirmé par le commentaire du code source lui-même ("mode diff... histoire bornée côté serveur ; mode full... renvoie tout à chaque update").
- ⚠→✓ **Correction empirique (premier run réel, T1.1, pas une lecture de code)** : `lines` n'est **pas append-only**. Le texte d'un index existant continue de grandir sur de nombreux polls successifs — une phrase entière peut rester à l'index 0 pendant plus de 60s de flux avant qu'une nouvelle ligne n'apparaisse à l'index 1. Le premier harnais suivait uniquement `len(lines)` (`known_line_count`), en s'appuyant sur une lecture de code qui semblait indiquer un append simple ; en pratique ça ratait silencieusement toute la croissance après le tout premier événement (1 seule ligne loggée sur ~60s de transcription active et correcte). Corrigé par un diff de texte par index (`bench/line_tracking.extract_updates`), avec `end` (qui avance à chaque mise à jour) comme référence temporelle au lieu de `start` (figé au début du segment). Leçon : une lecture de code source, même directe, reste une hypothèse tant qu'elle n'a pas tourné contre un flux réel.

~ Pocket TTS (Kyutai), d'après `pypi.org/project/pocket-tts` et le README du repo (pas de lecture du code source cette fois, à faire avant l'implémentation réelle en T2.2) : `TTSModel.load_model()` une fois, puis `model.generate_audio_stream(...)` par segment de texte, avec les embeddings de voix chargés via `import_model_state("voix.safetensors")` — confirme et précise ADR-0036 (pas de serveur, appel direct).

## Decision

Loom tourne dans **un seul process Python asyncio**. WhisperLiveKit et Pocket TTS sont importés comme bibliothèques, pas lancés comme serveurs. Aucun WebSocket, aucune sérialisation JSON, aucun aller-retour réseau (même loopback) entre les composants du pipeline. L'orchestrateur appelle directement `AudioProcessor.process_audio()` / consomme `AudioProcessor.create_tasks()`, et appelle directement `TTSModel.generate_audio_stream()`.

L'inférence TTS (bloquante, CPU) doit être déportée dans un executor (`asyncio.to_thread` ou équivalent) pour ne pas bloquer la boucle événementielle pendant que WLK continue à traiter le flux entrant — point d'implémentation à traiter en T2.2/T2.3, pas un changement de cette décision.

## Consequences

- Le budget "Transport + lecture audio" (100ms) tombe à quasi zéro en usage normal (appels de fonction directs) — cette marge n'est pas retirée du budget total tant qu'elle n'est pas mesurée et documentée comme telle (règle "mesurer avant d'optimiser") ; elle sert de coussin de sécurité pour la latence de scheduling asyncio (executor TTS, etc.).
- Le scaffold T0.1 est simplifié : un seul environnement (`env-wlk/` et `env-tts/` fusionnés) au lieu de deux, un seul service Docker Compose au lieu de deux. `env-wlk`/`env-tts` sont supprimés au profit d'un environnement unique.
- `docker-compose.yml`, `Dockerfile`, `harness.py` du scaffold T0.1-T0.4 sont réécrits en conséquence.
- Perte d'isolation process : un crash dans l'inférence TTS peut affecter le process WLK et réciproquement. Accepté pour un POC — à re-questionner si Loom sort du stade POC.
- Pour la cible future Raspberry Pi 5 + AI HAT+2 (T4.4) : reste à valider que WLK (accélération NPU via AI HAT) et Pocket TTS (CPU) peuvent cohabiter dans le même process sur cette plateforme — pas de raison de douter a priori (l'AI HAT est un accélérateur matériel local, pas un service réseau), mais non testé.
- `docs/corpus`/`bench/replay.py` restent valables tels quels : `replay_realtime` prend un callable `send` générique, il suffit de le brancher sur `audio_processor.process_audio` au lieu de `websocket.send` (aucune réécriture nécessaire de `replay.py`, `clock.py`, `corpus.py`, `instrumentation.py`, `aggregate.py`).

## Alternatives considérées

- **Garder 2 process séparés (WLK / TTS) par sockets Unix locaux au lieu de WebSocket** : rejeté — réduit un peu l'overhead réseau vs WebSocket/TCP mais garde une sérialisation et un context switch process-à-process, sans bénéfice d'isolation suffisant pour en justifier le coût sur ce budget de latence serré. Pas de raison de séparer WLK et TTS en process distincts puisque les deux tournent de toute façon sur la même machine et n'ont pas de conflit de dépendances connu (contrairement à `diarization-sortformer`/`voxtral-hf`, cf. ADR-0034, qui reste un conflit interne à WLK et n'implique pas de split process).
- **Garder l'architecture serveur (2 process + WebSocket) pour préparer un futur découpage réseau (ex. WLK sur une machine, TTS sur une autre)** : rejeté — aucun besoin actuel ou prévu de déploiement multi-machines ; sur-ingénierie pour un POC dont l'objectif est justement de tenir un budget de latence serré.

## Révisions

- 2026-07-07 — création
- 2026-07-07 — remplacement des points `~` (doc résumée) par une vérification directe du code source de WhisperLiveKit ; correction d'un bug réel trouvé dans `harness.py` par cette vérification : `pcm_input=True` manquait, ce qui aurait fait router notre PCM brut vers FFmpeg au lieu du chemin PCM direct.
- 2026-07-14 — premier run réel sur machine cible (RTX 5090, Python 3.12) : pipeline STT fonctionnel de bout en bout (transcription anglaise fidèle au texte source), mais bug de suivi des lignes découvert (`lines` pas append-only, cf. ci-dessus) — corrigé dans `harness.py`/`line_tracking.py`. Bugs d'environnement résolus au passage : Python 3.14→3.12 (headers Triton manquants sur 3.14), symlink `libcublas.so.13`→`.so.12` (CUDA 13 vs ctranslate2), VRAM occupée par `llama-server.service` tournant en fond.
