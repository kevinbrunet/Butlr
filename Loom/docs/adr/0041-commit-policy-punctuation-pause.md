# ADR 0041 — Politique de commit orientée ponctuation+pause, découplée des lignes WLK

## Status

Accepted

## Context

La traduction (SeamlessM4T v2, Phase 1 de [ADR 0040](0040-seamless-m4t-replaces-nllb-translation.md)) n'est pas incrémentale : elle a besoin d'une unité de parole cohérente et complète pour traduire correctement. Couper au milieu d'une phrase risque de produire une traduction fausse ou incohérente — préoccupation soulevée par Kevin le 2026-07-15 en réaction au premier run bout-en-bout réel.

La première version de `bench/harness_pipeline.py` scellait un "tour" dès qu'un nouvel index apparaissait dans `lines` (WLK, mode "full"). Constaté empiriquement sur le corpus `a` (narrateur continu, 185s, un seul `speaker_id`) : `lines` reste à 1-2 entrées pendant tout le fichier — le premier scellement (bug depuis corrigé) coupait à ~6s, et une fois corrigé, produisait un seul tour d'environ 2000 mots envoyé en un bloc vers Seamless puis Pocket TTS. Ni l'un ni l'autre n'est exploitable pour de l'interprétariat "simultané".

✓ Cause racine confirmée par lecture directe du code source (`whisperlivekit/tokens_alignment.py`, méthode `get_lines()`, lu le 2026-07-15) : en mode diarisation (notre cas — nécessaire pour attribuer la bonne voix TTS par locuteur), une nouvelle ligne n'est créée **que** lorsque `speaker_id` change entre deux segments consécutifs. Il n'existe pas de découpage sur ponctuation ou sur pause en mode diarisation — ce comportement n'existe que côté "sans diarisation" (des tokens `Silence` explicites y déclenchent une nouvelle ligne), inutilisable pour Loom puisque la diarisation est requise.

Conséquence : les bornes de `lines` de WLK ne sont **pas** un signal utilisable pour "assez de parole pour traduire correctement", dès qu'un même locuteur parle sans interruption d'un autre locuteur — un cas courant en pratique (une personne qui fait un exposé continu). Il faut une politique de segmentation à nous, indépendante du découpage interne de WLK.

## Decision

Le commit d'un sous-segment (unité envoyée à Seamless) se fait au niveau de l'orchestrateur, en surveillant la croissance du texte d'une ligne WLK (déjà suivie via `line_tracking.extract_updates`), sous deux conditions conjointes :

1. Le texte accumulé de la ligne contient un signe de ponctuation de fin de phrase (`.`, `!`, `?`) apparu depuis le dernier scellement de cette ligne.
2. Ce signe est confirmé stable : le texte de la ligne n'a plus grandi depuis une fenêtre de pause (`COMMIT_PAUSE_S`, ~ valeur de départ 1,5-2s de temps audio, pas encore benchmarkée) — évite de sceller sur un point qui fait en fait partie d'une abréviation ou d'un nombre en cours de dictée.

Seul le texte depuis le dernier point scellé de cette ligne part vers Seamless (pas la ligne entière) — l'index de ligne WLK devient un simple conteneur surveillé, plus l'unité de traduction elle-même.

Si un changement de `speaker_id` WLK (nouvel index de ligne) survient avant qu'un sous-segment en attente ait scellé par ponctuation+pause, le texte en attente est scellé de force à ce moment — garde-fou pour ne jamais perdre de texte, cohérent avec la règle transverse "le passé est immuable, les révisions STT ne s'appliquent qu'au futur" (`Loom/CLAUDE.md`).

## Consequences

- Nouveau budget codé en dur (`COMMIT_PAUSE_S`) à mesurer, au même titre que les budgets de latence par étage (cf. convention "mesurer avant d'optimiser").
- Le plancher de latence d'un segment inclut désormais au minimum la fenêtre de confirmation de pause, en plus de la latence Seamless+TTS — à intégrer au budget bout-en-bout une fois mesuré.
- Dépend de la fiabilité de la ponctuation produite par WLK/Whisper dans la transcription — pas encore vérifiée empiriquement (point faible connu de certains ASR, en particulier sur des langues sources moins ponctuées ou de la parole accentuée, cf. corpus `d`).
- Ne résout pas complètement le cas d'un locuteur qui parle sans aucune pause détectable pendant une longue durée : le scellement forcé sur changement de locuteur reste le seul filet dans ce cas, laissant un segment potentiellement long. Un plafond de durée supplémentaire (indépendant de la ponctuation) n'est délibérément pas ajouté maintenant — pas encore observé comme un problème réel au-delà du corpus `a`, à ajouter si constaté.
- Remplace entièrement la logique "scellement sur nouvel index de ligne WLK" de la première version de `bench/harness_pipeline.py`, ce n'est pas un correctif incrémental.

## Alternatives considérées

- **Scellement sur pause seule (sans exiger de ponctuation)** : rejeté comme politique par défaut — plus réactif sur de la parole sans ponctuation claire, mais risque réel de couper une phrase qui continue après une hésitation ou une respiration, exactement la préoccupation soulevée par Kevin. Reste une piste de repli si la ponctuation WLK s'avère peu fiable en pratique.
- **Continuer à s'appuyer sur les lignes WLK (`speaker_id` seul)** : rejeté — confirmé structurellement incompatible avec un locuteur unique continu (cf. Context), et déjà corrigé une fois (bug de scellement prématuré) sans résoudre le problème de fond.
- **Passer directement à SeamlessStreaming (Phase 2 de ADR-0040)** : pas rejeté, seulement pas retenu maintenant — un modèle EMMA/à attention monotone résout ce problème nativement (la politique "lire/écrire" est apprise, pas heuristique), mais l'intégration SimulEval/fairseq2 reste un chantier à part entière. Cette politique de commit heuristique est un pas intermédiaire pour continuer à valider la Phase 1 (Seamless batch) sans attendre ce chantier, pas un remplacement de la décision Phase 2.
- **Découpage à durée fixe (timer)** : rejeté explicitement — c'est le problème soulevé en premier lieu par Kevin, risque élevé de couper au milieu d'une phrase.

## Révisions

- 2026-07-15 — création
