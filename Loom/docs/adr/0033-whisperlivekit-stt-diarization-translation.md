# ADR 0033 — WhisperLiveKit pour STT streaming + diarisation + traduction intégrées

## Status

Superseded by [ADR 0039](0039-single-process-embedded-libraries-no-websocket.md) — le choix de WhisperLiveKit comme moteur STT+diarisation+traduction reste valable, seule l'hypothèse "serveur + WebSocket" est remplacée par un usage bibliothèque en process (`TranscriptionEngine`/`AudioProcessor`).

## Context

Le POC nécessite un pipeline STT streaming (EN/ZH) + diarisation (identification locuteur) + traduction vers le français, avec un budget de latence bout-en-bout p95 de 1,5-2s (~1600ms répartis par étage, cf. `docs/architecture.md` — budget latence). Construire ces trois briques séparément (STT streaming custom, module de diarisation, appel à un moteur de traduction) demanderait d'orchestrer manuellement la synchronisation texte/speaker/timestamps en flux incrémental — un problème déjà résolu par WhisperLiveKit (WLK), qui expose ces capacités derrière un unique WebSocket JSON incrémental.

✓ WhisperLiveKit combine un backend STT streaming SimulStreaming (politique d'attente AlignAtt), une diarisation (Sortformer ou diart), et une traduction NLLB intégrée, avec sortie incrémentale `{texte_fr, speaker, timestamps}` sur WebSocket.

~ Le budget WLK de 1s pour "audio → texte FR commité + speaker" est plausible mais non garanti sur le hardware cible (RTX 5090) — la référence historique WhisperStreaming/LocalAgreement était de ~3,3s de latence moyenne sur GPU A40 ; SimulStreaming/AlignAtt fait mieux mais le chiffre exact reste à mesurer (T1.2).

## Decision

On utilise WhisperLiveKit comme unique serveur STT+diarisation+traduction, backend `simulstreaming`. On ne code pas de pipeline STT/diarisation/traduction custom : l'orchestrateur (notre code, cf. ADR-0037) consomme le flux WebSocket de WLK et ne fait que du routage/commit vers le TTS.

## Consequences

- Surface de code réduite au strict orchestrateur TTS + politique de commit.
- Dépendance forte à la stabilité et à la performance de WLK — si T1.2 ne passe pas le gate p95 < 1s, il faut renégocier le budget global avant de poursuivre (pas de plan B "pipeline custom" prévu au POC).
- Le format exact du JSON WebSocket avec diarisation + traduction actives simultanément n'est pas vérifié à ce stade (⚠, cf. T1.4) — à explorer en premier via la doc DeepWiki du repo WLK.
- La détection automatique de la langue source (`--language auto`) est à valider spécifiquement sur le chinois (⚠, risque documenté : la détection auto biaise vers l'anglais) ; repli = configuration manuelle EN/ZH par session si l'auto échoue (T2.4).
- **Perte de prosodie assumée** : le pipeline est en cascade avec un texte comme intermédiaire à chaque étape (audio → texte source → texte FR → audio synthétisé). La hauteur, l'emphase, le rythme et la charge émotionnelle de la parole source ne traversent pas ce texte — le TTS régénère sa propre prosodie à partir du texte FR, indépendamment de celle de l'audio original. Des modèles de traduction parole-à-parole directe (ex. SeamlessExpressive de Meta) évitent cette perte en conditionnant la génération sur l'acoustique source plutôt que sur du texte pur. Compromis accepté ici car Loom vise un rôle d'**interprète** (transmettre le sens, comme un interprète humain simultané) et non de **doublage** (reproduire l'interprétation vocale exacte) — mais à documenter clairement dans le rapport final (T4.4) comme limite connue, pas comme un défaut caché.

## Alternatives considérées

- **Pipeline custom (faster-whisper streaming + module de diarisation séparé + appel API de traduction)** : rejeté — réinvente une synchronisation texte/speaker/timestamps déjà résolue par WLK, sans bénéfice attendu pour un POC dont l'objectif est de valider la faisabilité latence, pas l'infrastructure STT.
- **whisper.cpp en mode streaming naïf (sans politique d'attente dédiée)** : rejeté — latence de re-décodage à chaque nouveau chunk trop élevée pour le budget cible ; SimulStreaming/AlignAtt existe spécifiquement pour éviter ce problème.
- **Seamless (Meta) — SeamlessStreaming/SeamlessExpressive, traduction parole-à-parole directe** : reconsidéré en cours de POC (préserve la prosodie source, évite la perte documentée ci-dessus) mais rejeté pour trois raisons cumulatives : (1) ✓ aucune diarisation intégrée — il faudrait quand même un étage Sortformer/diart devant, donc aucun gain sur la complexité qu'on cherche à éviter ; (2) ✓ latence annoncée ~2s pour son seul étage traduction (SeamlessStreaming), déjà au niveau du budget bout-en-bout cible du POC entier (1,5-2s) ; (3) ✓ licence CC BY-NC 4.0, non commerciale. Le point de préservation de prosodie reste valide comme piste si Pocket TTS déçoit en qualité perçue (T2.1) — à reconsidérer alors comme remplaçant du TTS spécifiquement, pas du pipeline STT+diarisation+traduction.

## Révisions

- 2026-07-07 — création
- 2026-07-07 — superseded par ADR 0039 : usage bibliothèque en process, pas de serveur WebSocket.
- 2026-07-14 — ajout de la perte de prosodie comme conséquence explicite, et de Seamless (Meta) comme alternative sérieusement reconsidérée puis rejetée (pas de diarisation, latence propre ~2s, licence non commerciale).
