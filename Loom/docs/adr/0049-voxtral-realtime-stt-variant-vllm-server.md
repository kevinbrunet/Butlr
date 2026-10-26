# ADR 0049 — Variante STT Voxtral Realtime, servie par vLLM hors du process Loom

## Status

Accepted — supersedes partiellement [ADR 0039](0039-single-process-embedded-libraries-no-websocket.md), pour la seule variante `--stt voxtral` (le STT y passe par un serveur local et un websocket) ; la variante `--stt wlk` reste régie par ADR-0039.

## Context

Mistral a sorti Voxtral 3 le 2026-10-20 (✓ [annonce](https://mistral.ai/news/voxtral-3/), lue le 2026-10-26). Vérifié le même jour sur l'organisation `mistralai` de Hugging Face (API `models?author=mistralai&search=voxtral`) :

- ✓ **Voxtral 3 n'a pas de poids publiés** : ni lien de téléchargement ni licence dans l'annonce, aucun modèle « 3 » chez `mistralai`. Il n'est utilisable que par l'API de Mistral, ou sur ses propres serveurs via l'équipe commerciale. Hors cible pour Loom, qui tourne en local.
- ✓ **`mistralai/Voxtral-Mini-4B-Realtime-2602`** (janvier 2026) : Apache 2.0. ✓ D'après la fiche du modèle : streaming natif (encodeur audio causal, attention à fenêtre glissante), délai réglable de 240 ms à 2,4 s (480 ms recommandé, `transcription_delay_ms` dans `tekken.json`). Taux d'erreur moyen sur FLEURS (13 langues) : 10,8 % à 240 ms, 8,7 % à 480 ms, 7,7 % à 960 ms, 6,7 % à 2,4 s. Langues dont anglais, français et **chinois**. Ni diarisation ni traduction. GPU d'au moins 16 Go en BF16 selon la fiche. Servi par vLLM (`/v1/realtime`, websocket, voie recommandée) ; Transformers ≥ 5.2 annoncé, mais la fiche se contredit ailleurs (« seulement vLLM »).
- ✓ **`mistralai/Voxtral-4B-TTS-2603`** (mars 2026) : CC BY-NC 4.0, 9 langues dont le français, 20 voix prédéfinies. ⚠ Aucune source ne confirme le clonage par audio de référence avec les poids ouverts : la documentation de vLLM-Omni ne décrit que les voix prédéfinies. Incompatible en l'état avec le clonage progressif (ADR-0046, ADR-0048) : non retenu.

Kevin veut comparer, en mono comme en multi (ADR-0048), le STT actuel (WhisperLiveKit) et Voxtral Realtime. Deux contraintes pèsent sur l'intégration :

- ✓ `multi/env-loom/uv.lock` fige transformers 4.57.6 (WhisperLiveKit, NeMo) ; Voxtral demande Transformers 5.x. Les deux ne cohabitent pas dans un même environnement sans risque de casser l'existant.
- ✓ vLLM est la seule voie d'exécution que la fiche présente comme prête pour la production.

## Decision

Voxtral Realtime tourne **dans un serveur vLLM local**, avec son propre environnement (`Loom/voxtral-server/`, `serve.sh`), partagé par mono et multi. Loom en est un simple client websocket : `stt_voxtral.VoxtralRealtimeProcessor` imite l'interface de `whisperlivekit.AudioProcessor` telle que les orchestrateurs l'utilisent (`process_audio(bytes)`, `create_tasks()`, lignes `{"text", "end", "speaker"}`), si bien que le choix se fait par `--stt voxtral` sans toucher au pipeline de commit. Seul le STT change : traduction (Qwen) et TTS (Pocket TTS à voix clonées) restent les mêmes dans les deux variantes.

## Consequences

- Les environnements restent séparés : Loom garde transformers 4.57 ; le serveur a vLLM 0.32.0 et transformers 5.19.0 (✓ fedora3, 2026-10-26, torch 2.13.0+cu130 ; vLLM 0.32.0 exige transformers < 5.20).
- ✓ Protocole lu dans l'exemple officiel vLLM `examples/speech_to_text/realtime` : `session.created`, puis `session.update` (modèle), `input_audio_buffer.commit` (prêt), `input_audio_buffer.append` (PCM16 16 kHz en base64), `input_audio_buffer.commit` avec `final: true` (fin) ; en retour `transcription.delta`, `transcription.done`, `error`.
- ⚠ **Voxtral ne renvoie ni horodatage ni découpage en lignes.** L'adaptateur les reconstruit :
  - une ligne par phrase, scellée dès la ponctuation forte suivie d'un blanc, ou après 1,5 s d'audio sans nouveau texte (rôle d'une fin de tour WLK) ;
  - `end` = audio envoyé au moment du fragment, moins le délai de 480 ms.
  Ni l'un ni l'autre n'est validé sur corpus : les latences mesurées avec `--stt voxtral` dépendent de cette estimation.
- Une couche websocket en loopback s'ajoute sur le chemin critique, ce qu'ADR-0039 voulait éviter. ⚠ Coût non mesuré.
- **VRAM** : vLLM réserve par défaut 90 % de la carte ; `serve.sh` le borne à 0,50 avec `--max-model-len 8192` (~11 min de session selon la fiche, qui indique ≥ 45 000 jetons pour 1 h). ✓ Mesuré sur la 5090 : serveur ~16 Go, 17,8 Go au total pendant un run multi.
- En multi, chaque identité suivie ouvre sa propre session Voxtral, comme elle avait son propre `AudioProcessor` WLK (ADR-0044). La diarisation Sortformer disparaît dans cette variante ; le routage par identité, en amont, n'en dépendait pas avec SepFormer.
- Le chinois redevient possible côté STT (Voxtral le transcrit, en streaming) ; la traduction ZH → FR reste à revoir (ADR-0047 l'avait mise en pause).
- Le délai de transcription se règle côté serveur (`tekken.json`), pas par session : comparer 480 et 960 ms demande deux lancements du serveur.

## Alternatives considérées

### Transformers dans le process Loom (conforme à ADR-0039)

Pas de serveur ni de websocket. Rejeté : Transformers 5.x entre en conflit avec l'environnement figé de Loom (4.57, WhisperLiveKit, NeMo), et la fiche ne garantit pas que le chemin Transformers soit au niveau de vLLM.

### L'extra `voxtral-hf` de WhisperLiveKit

WhisperLiveKit sait déjà utiliser un Voxtral via Hugging Face. Rejeté : ✓ cet extra est incompatible avec `diarization-sortformer` (cf. `Loom/CLAUDE.md`, ADR-0034), et ⚠ rien n'indique qu'il prenne en charge le modèle Realtime 2602 plutôt que les Voxtral Mini de 2025.

### Voxtral 3 par l'API de Mistral

Streaming annoncé sous 500 ms, diarisation en lot. Rejeté : cloud, poids fermés ; Loom vise le local (Jetson à terme).

### Remplacer aussi le TTS par Voxtral-4B-TTS

Rejeté : licence non commerciale, clonage par référence non documenté avec les poids ouverts, au moins 16 Go de VRAM de plus selon la fiche.

## Révisions

- 2026-10-26 — création. Adaptateur et tests unitaires du découpage en lignes ; serveur installé.
- 2026-10-26 — premiers runs (fedora3, RTX 5090, Pocket TTS 4.0.1, voix par défaut, cf. ADR-0050) :
  - ✓ **Démarrage du serveur** : refusé avec 0,40 / 16 384 (cache KV de 5,72 Gio nécessaire, 2,18 Gio disponibles) ; puis échec de compilation à la volée de l'échantillonneur FlashInfer (`curand.h` absent, pas de toolkit CUDA système). Réglages retenus : 0,50 / 8 192 et `VLLM_USE_FLASHINFER_SAMPLER=0`.
  - ✓ **Sonde isolée (corpus `a`)** : le texte suit l'audio à moins de 0,1 s, mais **au bout de ~20 s Voxtral cesse de ponctuer et de mettre des majuscules**. Sans ponctuation, aucune phrase n'était scellée et `compute_flush` ne trouvait aucun point de coupe : rien n'était traduit pendant plus d'une minute (premier run mono). Correctif : la ligne active est scellée au dernier espace au-delà de 15 mots (⚠ non calibré). Effet de bord : des coupes au milieu des phrases, qui hachent parfois la traduction.
  - ✓ **Mono, corpus `a`, à conditions égales** : bout en bout p50/p95 892/2 524 ms avec Voxtral, contre 1 588/7 285 ms avec WLK (dans ce run, le TTS de la variante WLK était plus lent que le temps réel, facteur 0,81, et le retard s'accumulait). 50 incréments traduits contre 17.
  - ✓ **Multi, corpus `b`** : bout en bout p50/p95 1 495/8 402 ms avec Voxtral, contre 7 259/66 379 ms avec WLK ; 4 identités pour 2 locuteurs dans les deux cas (suivi d'identité, ADR-0044).
  - ⚠ Les latences STT de la variante Voxtral reposent sur la fin *estimée* (audio envoyé moins 480 ms) : elles ne se comparent pas strictement à celles de WLK. Qualité de transcription et de traduction non mesurée (pas de diff contre les références manuscrites pour ces runs).
- 2026-10-26 — ✓ **Cause de la perte de ponctuation, trois essais** (sonde isolée, corpus `a`, 185 s, ponctuations / majuscules sur ~639 mots) : référence 7 / 13 ; `--tokenizer-mode mistral` 7 / 13 (sortie identique) ; délai 960 ms 2 / 12 ; **nouvelle session toutes les 20 s : 37 / 56**. La dérive est durable dans une session et une session neuve la corrige, ce qui est cohérent avec l'ornière auto-entretenue décrite dans [vllm#47614](https://github.com/vllm-project/vllm/issues/47614) (jetons vides plutôt que texte sans ponctuation, non encore corrigé en amont). Aucun signalement public de notre symptôme exact (recherche du 2026-10-26 : tickets vLLM, discussions du modèle sur Hugging Face). Correctif : l'adaptateur rouvre sa session au bout de 20 s, de préférence pendant une pause de 0,6 s, au plus tard à 30 s (`should_rotate`) ; l'ancienne session termine son texte, les fragments de la nouvelle attendent pour garder l'ordre. Via l'adaptateur : 35 / 55, 635 mots, 7 rotations ; la coupe de sécurité passe de 15 à 30 mots. `--tokenizer-mode mistral` ajouté à `serve.sh` (exigé par la recette vLLM, sans effet mesuré).
- 2026-10-26 — ✓ **Matrice avec tous les correctifs** (TTS sur GPU ADR-0051, rotation, silence aux identités inactives ADR-0044), bout en bout p50/p95 : mono corpus `a` 546/724 ms (Voxtral) contre 758/1 321 ms (WLK) ; multi corpus `b` 873/2 317 ms (Voxtral) contre 1 630/5 542 ms (WLK). Aucune exception, aucune synthèse coupée. ⚠ Latences STT de Voxtral toujours fondées sur la fin estimée ; qualité non mesurée contre les références manuscrites.
