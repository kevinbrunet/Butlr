# ADR 0040 — SeamlessM4T v2 remplace NLLB pour la traduction, en 2 phases

## Status

Accepted — supersede partiellement [ADR 0035](0035-nllb-translation-not-llm.md) (le choix NLLB, pas le principe "pas de LLM généraliste")

## Context

Le premier run réel de T1.1 (185s de corpus `a`, 2026-07-14) a confirmé que le pipeline STT de WLK (SimulStreaming) transcrit fidèlement l'anglais source, mais que la traduction FR intégrée (NLLB, via le sous-package `nllw` de WLK) produit une hallucination récurrente d'un même mot ("voiture"/"auto" substitué à des mots sans rapport : "bank", "book", "well", "White Rabbit"...), **identique avec `nllb_size="600M"` et `"1.3B"`** — donc pas un problème de taille/qualité du modèle NLLB lui-même.

✓ Vérifié par lecture directe du code source de `nllw` (`QuentinFuxa/NoLanguageLeftWaiting`) : la cause la plus probable est `_continue_generation_with_cache()` (`nllw/core.py:370-416`), une optimisation maison qui réutilise un cache décodeur d'un appel précédent contre une représentation encodeur fraîchement recalculée sur un texte plus long — un pattern connu pour produire des insertions de mots sans rapport avec la source en décodage transformer incrémental. L'hypothèse initiale (la chaîne littérale `lan="auto"` qui fuiterait comme token de langue) a été vérifiée et **infirmée** par lecture du code : `nllw/core.py:77-83` exclut explicitement `"auto"` avant de le passer au tokenizer.

✓ Confirmation indépendante : le mainteneur de WhisperLiveKit a lui-même ajouté un **second backend de traduction** (`--translation-backend alignatt`, PR/issue #382, shippé dans WLK v0.2.24 — la version installée) spécifiquement pour ce type de problème : un LLM (Gemma) piloté par une politique d'alignement d'attention qui ne commit un mot cible que quand son attention retombe sur des mots source déjà commités. Ce backend a été évalué et rejeté pour cet usage : ~40 Go de VRAM (vLLM + Gemma-4-E4B, au-delà des ~31 Go de la RTX 5090), couverture de langues calibrées limitée à EN→{de,it,zh,cs,fr} (le repli léger Qwen3-1.7B ne couvre que EN→de, pas FR), et changement de backend ASR requis (`qwen3-streaming` au lieu de `simulstreaming`, remettrait en cause ADR-0033).

✓ Recherche sur Meta Seamless (cf. discussion en chat, sources : [ai.meta.com](https://ai.meta.com/resources/models-and-libraries/seamless-communication-models/), [HuggingFace facebook/seamless-m4t-v2-large](https://huggingface.co/facebook/seamless-m4t-v2-large), [transformers docs](https://github.com/huggingface/transformers/blob/main/docs/source/en/model_doc/seamless_m4t_v2.md)) :
- `SeamlessM4Tv2ForSpeechToText` (bibliothèque `transformers`, mainstream, déjà une dépendance de WLK) prend de l'audio 16kHz en entrée et produit du texte traduit directement, sans vocoder — **~5,82 Go de VRAM en fp16**, largement compatible avec la RTX 5090 en parallèle de WLK et Pocket TTS.
- Couverture ~100 langues des deux côtés (EN→FR et ZH→FR sans repli dégradé, contrairement à AlignAtt).
- N'est **pas** un modèle streaming mot-à-mot : traduit un segment audio complet (ex. un tour de parole entier), pas un flux incrémental.
- Licence CC BY-NC 4.0 (~ à reconfirmer si Loom devient autre chose qu'un usage personnel/recherche).

✓ La variante vraiment streaming (`SeamlessStreaming`, checkpoint `facebook/seamless-streaming`, 2,5 Md de paramètres) existe et a une couverture de langues comparable, mais son inférence dépend de **SimulEval** — un harnais d'évaluation de recherche (pas conçu pour du service temps réel, ne supporte pas le batching) — et de `fairseq2` (écosystème de recherche Meta, séparé de `transformers`). Meta héberge un Space HuggingFace fonctionnel (`facebook/seamless-streaming`, dossier `seamless_server/`) qui câble déjà SimulEval en serveur temps réel — code de référence réel et réutilisable, mais qui embarque sa propre ASR (openai-whisper, pas WLK), un serveur Flask/gevent/websocket à adapter vers notre process unique (ADR-0039), et dont la fraîcheur de maintenance depuis la sortie initiale de Seamless (fin 2023) n'est pas confirmée. Ne fait pas disparaître la dépendance SimulEval/fairseq2, l'enrobe seulement.

## Decision

Deux phases, décidées ensemble mais implémentées séparément :

1. **Phase 1 (maintenant)** : `SeamlessM4Tv2ForSpeechToText` remplace NLLB. WLK reste responsable du STT et de la diarisation (segmentation par locuteur/tour de parole) ; dès qu'un tour de parole est détecté comme terminé, le segment audio complet part vers Seamless pour traduction (pas de traduction incrémentale mot-à-mot à ce stade). La sortie texte de Seamless est envoyée à Pocket TTS — on ne branche jamais le module Expressive de Seamless (pas de vocoder Seamless), Pocket TTS garde la main sur la synthèse et le clonage de voix par locuteur (ADR-0036).
2. **Phase 2 (plus tard, si la latence par tour de parole s'avère insuffisante)** : migrer vers `SeamlessStreaming` en s'appuyant sur le code du Space `facebook/seamless-streaming` comme référence, pour retrouver une traduction incrémentale plus proche du temps réel mot-à-mot.

## Consequences

- NLLB et le sous-package `nllw` de WLK ne sont plus utilisés du tout — la traduction sort entièrement du périmètre de WLK. WLK est utilisé uniquement pour STT + diarisation.
- Nouvelle dépendance : `transformers` avec support `SeamlessM4Tv2ForSpeechToText` (à vérifier que la version tirée par `whisperlivekit` est compatible), `sentencepiece` pour le tokenizer.
- La granularité de latence change : plus de mesure mot-à-mot (T0.2-T0.4 mesuraient la latence par mise à jour de ligne WLK) — la Phase 1 mesure une latence par tour de parole complet. Le harnais de bench (`bench/harness.py`) reste valable pour WLK seul (STT), mais un nouveau harnais est nécessaire pour mesurer le pipeline Seamless.
- Le budget de latence par étage (cf. `Loom/CLAUDE.md`) doit être révisé une fois la Phase 1 mesurée : la traduction n'est plus "incluse dans l'étage WLK à 1000ms" (hypothèse d'ADR-0033/0035, plus valide), c'est un étage séparé avec son propre budget à établir empiriquement.
- Diarisation (Sortformer/NeMo) reste un prérequis non résolu (T1.4) : sans elle, on ne peut détecter proprement les tours de parole par locuteur — la Phase 1 peut être testée en isolation sur des segments audio découpés manuellement en attendant, mais le pipeline complet en dépend.
- Licence CC BY-NC 4.0 : pas de blocage identifié tant que Loom reste un usage personnel/recherche (POC) — à retrancher explicitement si l'usage change.

## Alternatives considérées

- **AlignAtt (Gemma sidecar, `--translation-backend alignatt` de WLK)** : rejeté — VRAM (~40 Go > 31 Go disponibles), couverture de langues insuffisante sur le repli léger (pas de FR), changement de backend ASR requis. Cf. Context ci-dessus pour le détail.
- **Repli LLM local (llama.cpp, `llama-server` déjà en place pour carson)** : envisagé comme alternative avant la découverte de Seamless — resterait viable si Seamless échoue en Phase 1 (T1.3), mais pas retenu en premier choix car Seamless couvre nativement la traduction (pas de prompt engineering à faire) et a un chemin d'intégration mainstream via `transformers`.
- **`SeamlessStreaming` directement (sans passer par la Phase 1 batch)** : rejeté pour l'instant — dépendance SimulEval/fairseq2 non mainstream, intégration de plusieurs jours sur un framework jamais utilisé dans ce repo, alors que la Phase 1 donne une validation de qualité de traduction en un temps bien plus court. Gardé comme Phase 2 documentée, pas abandonné.

## Révisions

- 2026-07-14 — création
