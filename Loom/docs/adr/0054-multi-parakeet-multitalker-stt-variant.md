# ADR 0054 — Multi : variante STT Parakeet Multitalker, qui transcrit chaque locuteur depuis le mélange sans séparation de voix

## Status

Accepted

## Context

Le multi isole les voix en amont du STT (ADR-0042/0044) : séparation aveugle (SepFormer ou PixIT), empreinte ECAPA, suivi d'identité en référentiel ouvert, puis un STT par identité. Sur le corpus `b` (deux lecteurs qui se chevauchent pendant ~45 s), le bilan du 2026-10-28 restait mauvais :
- 62 % de mots faux à la transcription avec Voxtral, 67 % avec WLK ;
- 44 et 63 % de mots perdus à la prononciation de la sortie ;
- la cause tient aux fuites de séparation et aux identités qui se mélangent, que chaque correctif de 0044 a réduites sans les supprimer.

Les pistes déjà essayées sont la diarisation, deux séparateurs aveugles, le suivi par empreinte, et l'extraction ciblée (OpenSpeakerBeam, WeSep). Une piste ne l'avait pas été : un modèle de transcription **entraîné sur de la parole qui se chevauche**.

✓ `nvidia/multitalker-parakeet-streaming-0.6b-v1` (fiche du modèle, lue le 2026-10-28) :
- licence NVIDIA Open Model License ;
- anglais seulement, ce qui suffit au périmètre EN→FR actif ;
- streaming en morceaux de 80 ms à 1,12 s ;
- guidé par la diarisation `nvidia/diar_streaming_sortformer_4spk-v2.1` (4 locuteurs au plus), il transcrit chaque locuteur directement depuis le mélange ;
- cpWER annoncé : 21,3 sur AMI IHM, 37,4 sur AMI SDM.

✓ NeMo est déjà dans l'environnement multi (commit 71820fbc, ADR-0048), qui contient `SpeakerTaggedASR`.

Banc isolé du 2026-10-28 (scratchpad, script d'exemple NeMo puis moteur au fil de l'eau, mêmes métriques que le bilan) :

| Corpus | Parakeet | Multi actuel, Voxtral | Multi actuel, WLK |
|---|---:|---:|---:|
| `b`, mots faux (segments fins, comme le bilan) | **50,5 %** | 61,8 % | 67,4 % |
| `e` (b + cafétéria, 10 dB) | 57,8 % | 55,1 % | 66,8 % |
| `f` (b + restaurant, 10 dB) | 64,8 % | 60,8 % | 72,1 % |
| `a` (un lecteur), mots faux | **1,5 %** | 2,6 % (mono) | 5,0 % (mono) |
| `h` (3 lecteurs à tour de rôle), mots attribués au bon lecteur | **99,2 %** | — | — |

- ✓ Coût de calcul : 67 ms (p95 77 ms) par morceau de 1,12 s. VRAM : ~6 Go pour les deux modèles.
- ✓ Avec des morceaux de 0,56 s, le corpus `b` passe à 57,8 % de mots faux : le gain de délai coûte trop cher en qualité.

## Decision

Le multi gagne une troisième variante STT, `--stt parakeet` (`stt_parakeet.py`), à côté de `wlk` (défaut) et `voxtral`. Avec elle :
- l'audio brut va à un moteur unique, `ParakeetMultitalkerEngine` (Sortformer streaming v2.1 + Parakeet Multitalker, morceaux de 1,12 s) ;
- ni séparation de voix, ni empreinte, ni routage ;
- chaque locuteur détecté par le diariseur devient une identité ;
- le texte de chaque locuteur est découpé en lignes au format de WLK (`ParakeetSpeakerView`, réutilise `SentenceLines` d'ADR-0049) ;
- traduction, synthèse, sortie et rattrapage sont inchangés.

Choix d'implémentation :
- features calculées au fil de l'eau par tranches qui se recouvrent : le préprocesseur du modèle n'a pas de normalisation (« NA »), le résultat est identique au calcul sur fichier entier à un mot près sur `b` ;
- décodeur glouton sans graphes CUDA ;
- ligne scellée après 2 s sans nouveau mot du locuteur ;
- `--voice-clone` refusé : il dépend de l'empreinte.

## Consequences

- ✓ Pipeline complet (2026-10-28, un run par corpus, Qwen3.5-9B, Pocket TTS, rattrapage actif) :

  | Corpus | Transcription | Traduction (chrF) | Prononciation | Sortie entendue (chrF) | Délai p50 / p95 |
  |---|---:|---:|---:|---:|---:|
  | `b` Parakeet | 52,2 % | **59,3** | 40,3 % | 38,0 | **462 / 4 077 ms** |
  | `b` Voxtral (actuel) | 61,8 % | 57,6 | 43,6 % | 47,0 | 906 / 2 426 ms |
  | `b` WLK (actuel) | 67,4 % | 51,9 | 63,1 % | 28,4 | 1 558 / 5 823 ms |
  | `e` Parakeet | 57,1 % | 41,4 | 13,6 % | 39,5 | 1 504 / 3 594 ms |
  | `e` Voxtral (actuel) | 55,1 % | 58,8 | 44,6 % | 45,3 | 1 085 / 4 511 ms |
  | `f` Parakeet | 58,8 % | 41,4 | 11,2 % | 41,4 | 1 454 / 3 539 ms |
  | `f` Voxtral (actuel) | 60,8 % | 54,9 | 44,6 % | 44,2 | 1 143 / 18 819 ms (3 530 ms après correctif, ADR-0049) |

  Corpus `h` :
  - 3 identités pour 3 lecteurs, 99 % des mots attribués au bon lecteur (86 % des segments ; les erreurs sont de courts fragments) ;
  - délai p50/p95 1 427 / 3 260 ms, contre 601 / 951 ms pour le `--speakers` du mono (ADR-0052) ;
  - prononciation 14,1 %.
- ✓ VRAM max du run : 16,4 Go, contre ~30 Go pour la variante Voxtral (serveur vLLM compris) et ~14,5 Go pour WLK.
- **Sans bruit, chevauchement franc (`b`) : meilleure transcription et meilleure traduction.** Les deux lecteurs sont suivis du début à la fin, sans identité fantôme.
- ⚠ **Dans le bruit (`e`, `f`) : pas de gain.** Le diariseur confond souvent les deux lecteurs pendant le chevauchement, et des passages entiers d'un lecteur disparaissent. Il y a moins de mots parasites que Voxtral, mais plus de contenu perdu, d'où un chrF de traduction de 41 contre 55-59.
- ⚠ **Délai structurellement plus long que Voxtral** : un mot n'existe qu'à la fin de son morceau de 1,12 s, et le point final arrive souvent un morceau plus tard. Le p50 varie de 0,46 à 1,5 s selon le corpus.
- ⚠ Les chiffres de prononciation et de sortie entendue en multi mesurent surtout la superposition des voix dans la sortie mixée (deux traductions jouées en même temps). Ils varient fortement d'un run à l'autre (`b` : 19 à 40 % sur trois runs).
- ⚠ Un seul run par corpus après les correctifs : la traduction est déterministe, mais le découpage en lignes dépend du minutage.
- ⚠ Quatre locuteurs au plus (diariseur `4spk`). Les features de toute la session restent sur le GPU (~180 Mo par heure), sans purge.
- ⚠ Licence NVIDIA Open Model License, à relire avant tout usage hors POC.
- ⚠ Le défaut de dernière ligne corrigé ici (une ligne scellée restait vue comme active, donc traduite seulement en fin de flux) existe aussi dans la variante Voxtral, qui partage `SentenceLines` : c'était la cause de son p95 de 18,8 s sur `f`. Corrigé le 2026-10-29 dans `SentenceLines.snapshot()`, commun aux deux variantes : p95 de 3,5 s sur `f` (ADR-0049).

## Alternatives considérées

### Dédoublonner le texte entre pistes après la séparation

On ne garde qu'une copie quand deux identités transcrivent les mêmes mots à quelques secondes d'écart. C'est peu coûteux, mais cela traite le symptôme (les mots en trop) sans récupérer les mots perdus. La séparation reste dans la boucle.

### Ne séparer que pendant les chevauchements

Cela réduit les artefacts hors chevauchement, mais garde la séparation aveugle et le suivi par empreinte là où ils échouent.

### Un meilleur séparateur (MossFormer2, ClearerVoice)

Il remplacerait PixIT dans l'architecture actuelle, avec deux voix au plus. Le problème de permutation et de suivi d'identité resterait entier.

### Extraction ciblée pré-entraînée (WeSep, USEF-TSE)

Maturité à vérifier, et ADR-0044 a déjà écarté OpenSpeakerBeam et WeSep (qualité insuffisante, entraînement nécessaire).

### Remplacer la séparation par défaut

Prématuré : Parakeet ne gagne pas dans le bruit, et son délai est plus long. Les trois variantes restent disponibles, et le défaut (`wlk`) est inchangé, conformément à la règle « mesurer avant d'optimiser ».

## Révisions

- 2026-10-28 — création. Banc isolé, `stt_parakeet.py` (moteur, vue par locuteur, tests des parties pures), câblage dans `multi/main.py`. Trois défauts trouvés et corrigés au premier passage dans le pipeline :
  - plantage CUDA du décodeur à graphes quand llama.cpp et Pocket TTS calculent en parallèle ;
  - horodatage en avance de quelques dizaines de ms sur l'horloge du rejeu, qui tuait en silence la tâche du locuteur (`LatencyEvent` refuse t_out < t_in) ;
  - pause de scellement plus courte qu'un morceau, qui hachait le texte.
