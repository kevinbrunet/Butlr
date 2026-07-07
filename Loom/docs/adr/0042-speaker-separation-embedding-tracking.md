# ADR 0042 — Séparation de voix (SepFormer) + suivi d'identité par embedding

## Status

Accepted

## Context

Constaté depuis le premier run réel de diarisation (T1.4, ADR-0034, révision du 2026-07-15) : la diarisation de WLK donne des **bornes temporelles** par locuteur (`speaker_id`, `[start, end]`), mais ne sépare pas les voix dans le signal audio lui-même. Pendant une zone de recouvrement, l'audio extrait pour un locuteur (via `bench/audio_chunks.read_segment`) contient toujours la voix de l'autre locuteur en fond — au moins une contamination de contenu a été observée sur le corpus `b` (propre, sans bruit ajouté), donc le problème existe indépendamment du bruit de fond testé sur `e`/`f`.

Revue de littérature et vérification pratique (2026-07-15, cf. discussion en chat) sur les techniques de séparation de voix :

- **Séparation aveugle** (SepFormer-WHAMR, SpeechBrain) : ✓ checkpoint pré-entraîné disponible, licence Apache 2.0, benchmarké (+13,7dB SI-SNRi sur WHAMR!, le jeu de données qui a servi à construire le bruit de `e`/`f`). Deux limites structurelles :
  - **Permutation non ancrée** : l'entraînement par *Permutation Invariant Training* ne garantit aucune correspondance stable entre un canal de sortie et un locuteur réel d'un appel à l'autre — rejouer la séparation sur une fenêtre différente peut inverser les deux voix sans prévenir.
  - **Coût quadratique en durée** (attention du transformer dual-path) : ~ la latence/mémoire augmente plus vite que la durée traitée. ~ Le modèle est entraîné sur des segments d'environ 5,6s (moyenne WHAMR!, saturation des performances vers 5,8s) — en dessous, moins de contexte que prévu ; largement au-dessus, coût disproportionné. Un segment continu de 185s (cf. corpus `a`) serait impraticable tel quel.
- **Extraction ciblée** (SpeakerBeam et dérivés) : architecture supérieure en principe — le calcul du masque est directement conditionné par un embedding de voix persistant, donc pas d'ambiguïté de permutation (la sortie est ancrée à l'identité demandée, pas à un numéro de canal arbitraire). Mais ⚠ aucune implémentation mûre et prête à l'emploi trouvée : `OpenSpeakerBeam-SS` (réimplémentation indépendante, revendique le temps réel via modélisation à état) mesure un SI-SNR de **-5,89dB** — pire que l'audio non traité, inutilisable ; `WeSep` (équipe reconnue, wenet-e2e) fournit le framework d'entraînement mais liste "modèles pré-entraînés" comme un objectif de roadmap, pas une livraison — entraîner nous-mêmes serait un chantier disproportionné pour ce POC.
- **Extraction d'embedding de locuteur** : ✓ mature et disponible (SpeechBrain ECAPA-TDNN/x-vector/ResNet, ou NeMo TitaNet — déjà dans la famille de dépendances via `nemo_toolkit`, utilisé pour Sortformer/ADR-0034). Vecteur de taille fixe, indépendant du contenu et de la durée (obtenu par pooling statistique sur les traits par trame), comparable par similarité cosinus — le même principe que le suivi d'identité déjà utilisé en diarisation par clustering.

Conclusion de cette revue : la seule pièce manquante pour l'extraction ciblée "propre" est le réseau de masquage conditionné par embedding — ni entraînable ni disponible dans un délai raisonnable. Toutes les autres briques (séparation aveugle mature, extraction d'embedding mature) sont disponibles dès maintenant.

## Decision

Approximer l'extraction ciblée en combinant les deux briques matures, sans attendre un modèle d'extraction ciblée publié :

1. **Séparation aveugle bornée en durée** : SepFormer-WHAMR tourne sur des fenêtres d'audio de durée plafonnée (quelques secondes, valeur exacte à calibrer empiriquement — jamais sur la durée complète d'une ligne WLK, pour éviter le coût quadratique constaté sur un monologue continu comme le corpus `a`).
2. **Suivi d'identité par embedding** : dès qu'une ligne WLK a accumulé assez d'audio propre (pas encore de chevauchement détecté) pour en tirer un embedding fiable (ECAPA-TDNN ou TitaNet), cet embedding est sauvegardé pour ce `speaker_id`. À chaque nouvelle fenêtre séparée, un embedding est extrait de chacun des deux flux de sortie et comparé (similarité cosinus) aux embeddings déjà connus des locuteurs actifs — ça résout la permutation sans modèle conditionné, au prix d'un bout de code de suivi (comparaison + réordonnancement), pas d'un nouveau modèle.
3. **Repli tant qu'il n'y a pas d'embedding fiable** : au tout début d'un tour de parole (ou tant que la séparation n'a pas encore tourné), le comportement reste identique à aujourd'hui — audio brut envoyé à `AlignAttSeamlessTranslator`/`SeamlessTranslator`. Aucune régression par rapport à l'existant, seulement une amélioration une fois qu'un embedding est disponible.
4. **Point d'insertion chirurgical** : la diarisation WLK n'est pas touchée (elle continue de tourner sur l'audio brut mélangé, avec ses limites connues, cf. ADR-0034) — la séparation+suivi d'identité s'insère uniquement entre `read_segment` (audio brut pour une ligne WLK) et l'appel à `translate`/`translate_partial` : c'est exactement le point où la contamination entre dans le pipeline aujourd'hui.

## Consequences

- Nouvelles dépendances : `speechbrain` (SepFormer-WHAMR + un extracteur d'embedding, ECAPA-TDNN), ou réutilisation de TitaNet (NeMo, déjà installé) pour l'embedding seul — à trancher à l'implémentation selon ce qui est le plus simple à faire cohabiter avec l'environnement `env-loom/` existant.
- Nouvel étage de calcul GPU/CPU dans le chemin critique, en plus de WLK+Seamless+Pocket TTS déjà en place — coût à mesurer avant intégration complète (cf. convention "mesurer avant d'optimiser"), pas supposé gratuit.
- Nouveau paramètre codé en dur à calibrer : la durée de fenêtre de séparation (compromis entre "assez de contexte pour que SepFormer soit fiable" et "pas trop pour éviter le coût quadratique").
- Le suivi d'identité par embedding est une approximation, pas une garantie : si deux locuteurs ont des voix très proches, ou si la séparation elle-même est de mauvaise qualité sur un passage donné, le suivi peut se tromper — pas de garantie formelle contrairement à une vraie extraction ciblée conditionnée.
- La diarisation WLK garde ses limites actuelles (elle-même tourne sur l'audio mélangé) — cet ADR améliore la qualité de ce qui est *envoyé à la traduction*, pas la détection des tours de parole elle-même.

## Alternatives considérées

- **Extraction ciblée avec modèle entraîné par nous (WeSep)** : rejeté pour l'instant — chantier d'entraînement disproportionné pour un POC, à reconsidérer si l'approche par embedding+séparation aveugle s'avère insuffisante.
- **OpenSpeakerBeam-SS tel quel** : rejeté — qualité mesurée insuffisante (SI-SNR négatif), projet trop jeune (14 étoiles, licence non tranchée).
- **Séparation par filtrage harmonique/pitch (CASA classique)** : pas rejeté, gardé comme repli rapide — ne nécessite aucun modèle externe, mais échoue sur les sons non voisés et les voix de hauteur proche, qualité probablement inférieure à SepFormer. À envisager seulement si SepFormer+embedding s'avère lui-même impraticable (coût ou qualité).
- **Ne rien faire (statu quo, diarisation seule)** : rejeté — contamination déjà documentée comme un problème réel (ADR-0034), pas hypothétique.

## Révisions

- 2026-07-15 — création
- 2026-07-15 — premier run réel (corpus `b`, `harness_pipeline.py` avec séparation activée) : ⚠ régression de latence sévère constatée — étage `seamless` avec 44 dépassements de budget sur 50 (1000ms), p95 bout-en-bout à 13,6s (cible 1,5-2s). Seulement 4 increments produits pour tout le fichier (85s, 2 locuteurs), contre plusieurs dizaines sans séparation — le pipeline décroche visiblement du temps réel. Qualité de traduction également dégradée (hallucinations, ex. "C'est un truc qui est très drôle" sans rapport avec le texte source), probablement un symptôme du décrochage (des increments consécutifs travaillent sur des quantités d'audio très différentes faute de suivre le rythme) plutôt qu'un bug distinct. Cause pas encore isolée entre (a) le coût propre de SepFormer/ECAPA-TDNN et (b) la contention GPU (5 modèles chargés simultanément : WLK, Sortformer, Seamless, Pocket TTS, SepFormer, ECAPA) — `bench/harness_separation.py` ajouté pour mesurer (a) en isolation avant de décider quoi optimiser, cf. convention "mesurer avant d'optimiser".
- 2026-07-15 — `harness_separation.py` exécuté (corpus `b`, fenêtre 6s, 3 répétitions) : ✓ (a) est écarté — coût de séparation+embedding négligeable une fois "réchauffé" (premier appel 374ms+219ms, coût classique de compilation JIT CUDA au premier appel ; appels suivants ~30ms+10ms, largement dans le budget). Le suspect principal devient (b), la contention GPU entre les 5 modèles — reste à confirmer par une mesure d'utilisation GPU (`nvidia-smi`) pendant un run complet avant d'investir dans une sérialisation des appels GPU côté orchestrateur.
- 2026-07-15 — `nvidia-smi -l 1` observé pendant un run complet réel : (b) est **aussi écarté** — utilisation GPU restée basse (17-21%) tout du long, pas de saturation de calcul. En revanche ✓ fuite mémoire GPU nette constatée entre deux relevés à 30s d'écart : 13,9 Go → 29,85 Go de VRAM utilisée (sur 32,6 Go) par le seul process Python du pipeline — la vraie cause probable du ralentissement (pression sur l'allocateur CUDA à l'approche de la limite, pas un calcul plus lourd). Suspect identifié : `LineCommitState.voice_state` (état de continuation Pocket TTS, cf. ADR-0041) jamais libéré après le scellement définitif d'une ligne — corrigé (`_release_gpu_state`, appelé à la fin de `force_final_commit`, remet `voice_state`/`audio_chunks` à vide et appelle `torch.cuda.empty_cache()`). Pas encore revalidé sur la machine cible.
