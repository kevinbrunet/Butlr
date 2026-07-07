# ADR 0041 — Politique de commit AlignAtt (attention Seamless), découplée des lignes WLK

## Status

Accepted — révisée le 2026-07-15 avant toute implémentation (cf. Révisions) : la politique de commit texte (ponctuation+pause) est remplacée par AlignAtt, appliqué directement à l'attention croisée de Seamless.

## Context

La traduction (SeamlessM4T v2, Phase 1 de [ADR 0040](0040-seamless-m4t-replaces-nllb-translation.md)) n'est pas incrémentale : elle a besoin d'une unité de parole cohérente et complète pour traduire correctement. Couper au milieu d'une phrase risque de produire une traduction fausse ou incohérente — préoccupation soulevée par Kevin le 2026-07-15 en réaction au premier run bout-en-bout réel.

La première version de `bench/harness_pipeline.py` scellait un "tour" dès qu'un nouvel index apparaissait dans `lines` (WLK, mode "full"). Constaté empiriquement sur le corpus `a` (narrateur continu, 185s, un seul `speaker_id`) : `lines` reste à 1-2 entrées pendant tout le fichier — le premier scellement (bug depuis corrigé) coupait à ~6s, et une fois corrigé, produisait un seul tour d'environ 2000 mots envoyé en un bloc vers Seamless puis Pocket TTS. Ni l'un ni l'autre n'est exploitable pour de l'interprétariat "simultané".

✓ Cause racine confirmée par lecture directe du code source (`whisperlivekit/tokens_alignment.py`, méthode `get_lines()`, lu le 2026-07-15) : en mode diarisation (notre cas — nécessaire pour attribuer la bonne voix TTS par locuteur), une nouvelle ligne n'est créée **que** lorsque `speaker_id` change entre deux segments consécutifs. Il n'existe pas de découpage sur ponctuation ou sur pause en mode diarisation — ce comportement n'existe que côté "sans diarisation" (des tokens `Silence` explicites y déclenchent une nouvelle ligne), inutilisable pour Loom puisque la diarisation est requise.

Conséquence : les bornes de `lines` de WLK ne sont **pas** un signal utilisable pour "assez de parole pour traduire correctement", dès qu'un même locuteur parle sans interruption d'un autre locuteur — un cas courant en pratique (une personne qui fait un exposé continu). Il faut une politique de segmentation à nous, indépendante du découpage interne de WLK.

✓ Revue de littérature (2026-07-15, cf. discussion en chat) sur les politiques read/write en traduction simultanée de la parole : les approches à seuil fixe (timer, ponctuation seule) sont documentées comme structurellement fragiles — "les frontières fixes ne s'alignent pas avec les fins naturelles de prononciation, perturbant l'intégrité acoustique" (survey [arxiv 2406.00497](https://arxiv.org/html/2406.00497v1)). Une famille alternative existe et a été validée **directement sur SeamlessM4T** : **AlignAtt** (Papi et al., Interspeech 2023, [arxiv 2305.11408](https://arxiv.org/abs/2305.11408)) lit l'attention croisée du décodeur pendant la génération — un token traduit est "sûr" à émettre si son attention pointe vers de l'audio source suffisamment ancien (pas les toutes dernières frames disponibles), sans nécessiter de ré-entraînement du modèle. **SimulSeamless** (FBK, IWSLT 2024, [arxiv 2406.14177](https://arxiv.org/html/2406.14177v1)) applique déjà AlignAtt à SeamlessM4T (medium) : ~1,8-2,0s de latence (AL), BLEU compétitif sur la plupart des paires de langues, code ouvert (`hlt-mt/FBK-fairseq`, Apache 2.0) mais bâti sur `fairseq`/SimulEval, pas directement branchable sur notre `transformers.SeamlessM4Tv2ForSpeechToText`. ⚠ Constat notable : les logs WLK de nos propres runs (`whisperlivekit.simul_whisper.align_att_base`) montrent que **le STT de WLK utilise déjà AlignAtt en interne** pour sa propre politique de commit — on avait la même famille de méthode sous les yeux depuis le premier run sans la réutiliser côté traduction.

~ Repère de latence : l'*ear-voice span* des interprètes professionnels tourne généralement autour de 2-4s (une méta-étude cite une moyenne <3s) ; Seed LiveInterpret 2.0 (ByteDance, système de production avec clonage vocal temps réel) revendique ~3s. La cible de 1,5-2s p95 de Loom est donc ambitieuse même face à l'état de l'art industriel — AlignAtt+Seamless (~1,8-2s mesuré côté traduction seule dans SimulSeamless) est cohérent avec cette cible, contrairement à une heuristique texte qui ajoute un coût fixe supplémentaire par-dessus.

## Decision

La politique de commit d'un sous-segment (unité envoyée à la synthèse TTS) est **AlignAtt appliqué directement à l'attention croisée de `SeamlessM4Tv2ForSpeechToText`**, pas une heuristique sur le texte WLK (ponctuation+pause, envisagée initialement — cf. Alternatives).

À chaque poll où le texte d'une ligne WLK a grandi, l'audio source disponible pour cette ligne (du début de la ligne à son `end` courant) est **entièrement retraduit depuis zéro** (ré-encodage complet, pas de réutilisation de cache décodeur entre appels à audio de longueur différente — reproduire ce pattern serait reproduire le bug de `_continue_generation_with_cache` qui a cassé NLLB, cf. [ADR 0040](0040-seamless-m4t-replaces-nllb-translation.md)) via `generate(..., output_attentions=True, return_dict_in_generate=True)`. Pour chaque token généré, la frame source la plus attendue (argmax de l'attention croisée de la dernière couche décodeur, moyennée sur les têtes) détermine s'il est "sûr" : sûr si cette frame est à plus de `frontier_frames` de la fin de l'audio disponible, incertain sinon — dès le premier token incertain, tout le reste de la séquence générée est considéré incertain aussi (attention globalement monotone en parole, cf. papier original).

Seul le préfixe "sûr" nouvellement apparu depuis le dernier commit de cette ligne part vers Pocket TTS. Si le nouveau texte "sûr" ne préfixe pas exactement l'ancien texte déjà commité (l'attention a "changé d'avis" sur un token déjà émis), c'est loggué en WARNING et ignoré — l'ancien texte commité n'est jamais corrigé, cohérent avec "le passé est immuable" (`Loom/CLAUDE.md`), mais ça signale une violation de l'hypothèse de monotonie d'AlignAtt à surveiller.

Le scellement forcé sur changement de `speaker_id` WLK (nouvel index de ligne) reste inchangé par rapport à la version précédente de cet ADR : à ce moment, le texte restant (même incertain) est commité de force — filet de sécurité, pas la politique normale.

## Consequences

- `frontier_frames` (nombre de frames encodeur, pas une durée — évite de dépendre d'un taux de trame Seamless non vérifié) est un nouveau paramètre codé en dur à benchmarker, au même titre que les budgets de latence (cf. convention "mesurer avant d'optimiser") — ⚠ valeur de départ non calibrée, à ajuster empiriquement.
- Chaque poll ré-encode l'intégralité de l'audio disponible pour la ligne (coût qui grandit avec la durée de la ligne, contrairement à une traduction incrémentale "vraie") — accepté comme compromis pour éviter le bug de cache NLLB, mais le coût CPU/GPU cumulé sur une ligne longue doit être mesuré (T1.2-équivalent pour cette politique).
- Ne dépend plus de la fiabilité de la ponctuation WLK — supprime un point faible identifié dans la version précédente de cet ADR.
- Le scellement forcé sur changement de locuteur reste le seul filet pour un locuteur qui ne s'arrête jamais assez longtemps pour d'autres raisons (aucun changement sur ce point).
- Remplace la logique "attend la fin du flux" actuellement dans `bench/harness_pipeline.py` (aucune des deux versions de cet ADR n'a été implémentée avant celle-ci).

## Alternatives considérées

- **Ponctuation+pause sur le texte WLK (version initiale de cet ADR, 2026-07-15)** : rejetée avant implémentation, suite à la revue de littérature ci-dessus — une heuristique texte est un proxy aveugle à ce que Seamless "sait" réellement de son propre alignement audio-traduction, et ajoute un coût fixe de pause par segment que AlignAtt n'a pas (la décision se prend en continu pendant la génération, pas après une attente dédiée). Reste une piste de repli si AlignAtt s'avère peu fiable en pratique (ex. attention peu monotone sur certaines paires de langues).
- **Scellement sur pause seule (sans ponctuation)** : rejeté pour les mêmes raisons que ci-dessus, en plus faible.
- **Continuer à s'appuyer sur les lignes WLK (`speaker_id` seul)** : rejeté — confirmé structurellement incompatible avec un locuteur unique continu (cf. Context), et déjà corrigé une fois (bug de scellement prématuré) sans résoudre le problème de fond.
- **SimulSeamless tel quel (`hlt-mt/FBK-fairseq`)** : rejeté comme dépendance directe — bâti sur `fairseq`/SimulEval, pas `transformers`, changerait notre stack de traduction pour une dépendance de recherche non mainstream (le même type de coût qui avait fait rejeter `SeamlessStreaming` en Phase 2 de ADR-0040). L'**algorithme** AlignAtt est repris (ré-implémenté contre `transformers`), pas le code.
- **Passer directement à SeamlessStreaming (Phase 2 de ADR-0040)** : toujours pas retenu — AlignAtt est une étape intermédiaire moins coûteuse à intégrer (pas de SimulEval/fairseq2) qui, d'après SimulSeamless, atteint déjà une latence proche de notre cible. Phase 2 reste l'option si AlignAtt s'avère insuffisant.
- **Découpage à durée fixe (timer)** : rejeté explicitement — c'est le problème soulevé en premier lieu par Kevin, risque élevé de couper au milieu d'une phrase.

## Révisions

- 2026-07-15 — création (politique ponctuation+pause sur le texte WLK)
- 2026-07-15 — révision avant implémentation : remplace la politique par AlignAtt (attention croisée de Seamless), suite à une revue de littérature montrant que cette famille de méthode est déjà validée sur SeamlessM4T (SimulSeamless, FBK) et que WLK lui-même l'utilise déjà côté STT. Implémentation en cours (`loom_orchestrator/alignatt.py`, `translation_seamless.AlignAttSeamlessTranslator`).
