# ADR 0052 — Mono : identification par empreinte vocale des intervenants qui parlent à tour de rôle, une voix de synthèse par identité

## Status

Accepted

## Context

Le mono (ADR-0048) suppose un seul intervenant : une seule voix de sortie pour tout ce qui entre. Or un cas courant est entre les deux : plusieurs personnes au même micro qui parlent **chacune leur tour**, sans se couper (table ronde, questions-réponses, entretien). Le multi traite ce cas, mais au prix de la séparation de voix et de la diarisation (ADR-0042/0044), dont le coût en latence et en fragilité est précisément ce qui a motivé la scission (ADR-0048). Sans chevauchement, l'audio est propre, une seule voix à la fois : il n'y a rien à séparer, seulement à **reconnaître qui parle**.

ADR-0048 avait retiré speechbrain (donc l'encodeur ECAPA) du mono, et rejeté le registre inter-sessions, faute de besoin. Le besoin est maintenant là, mais à l'intérieur d'une session.

Mesures préalables (2026-10-28, corpus `h` construit pour l'occasion : 3 lecteurs LibriVox, 15 tours alternés, 186 s, pauses de 0,6 s entre tours, aucun chevauchement) :

- ✓ Similarité cosinus ECAPA (`speechbrain/spkrec-ecapa-voxceleb`) entre deux extraits pris au hasard : même lecteur, médiane 0,46 sur 1,5 s et 0,59 sur 2,5 s ; lecteurs différents, médiane ~0,06, maximum 0,40. En dessous de ~1,5 s les deux distributions se recouvrent.
- ✓ Coût : ~5 ms par segment sur RTX 5090.
- ✓ Fréquence fondamentale médiane (librosa `yin`) : lectrice 181-194 Hz, lecteurs ~110 Hz, mais une fenêtre isolée de 2,5 s déborde (146 à 239 Hz).
- ✓ Clonage indisponible pour choisir une voix ressemblante : le modèle Pocket TTS `french_24l` publié est « without voice cloning », et une voix clonée depuis l'anglais allonge l'audio 2 à 3 fois (ADR-0050). Les voix de sortie sont donc prises parmi les voix précalculées.
- ✓ Banc des 27 voix précalculées `french_24l` (20 incréments FR, retranscrits par faster-whisper large-v3) : 2,7 à 5,9 % de mots perdus pour les six retenues (stuart_bell, estelle, azelma, daan, jane, peter_yearsley), jusqu'à 18 % pour les pires ; javert intelligible mais anormalement lent.

## Decision

Le mono gagne une option `--speakers` (défaut : désactivée, comportement d'ADR-0048 inchangé ; incompatible avec `--voice-clone`). Chaque segment envoyé à la traduction est attribué à une identité d'après l'empreinte ECAPA de son audio source, et chaque identité reçoit sa propre voix précalculée Pocket TTS, choisie selon la hauteur de la voix source (pool féminin estelle/azelma/jane, pool masculin stuart_bell/daan/peter_yearsley, frontière 150 Hz).

Règles (`speaker_identity.py`) :

- Audio du segment = de la fin du segment précédent à sa fin (6 s au plus), réduit au plus long passage parlé entre deux pauses d'au moins 0,4 s, pour ne pas mélanger deux voix à un changement de tour.
- Référentiel ouvert, centroïde = moyenne cumulée des empreintes de l'identité. Reconnaissance si cosinus ≥ 0,35 et segment ≥ 1,5 s ; création d'une identité seulement si le segment fait au moins 2,5 s ; sinon, l'identité courante est gardée.
- Voix réévaluée une seule fois, sur la médiane des trois premières hauteurs mesurées.
- Le contexte de traduction est tenu par identité. La sortie reste un seul tampon : les tours sont joués dans l'ordre, sans superposition.

## Consequences

- ✓ Corpus `h`, 2 runs `--stt voxtral` et 2 runs `--stt wlk` (2026-10-28) : 3 identités pour 3 lecteurs sur les 4 runs, même attribution (Alice → estelle, Tom → stuart_bell, Moby → daan). 94 % des segments bien attribués (46/49, 44/47, 45/48), 98 % des mots en Voxtral, 94 % en WLK. Prononciation 3,7 à 6,1 % de mots perdus. Délai bout en bout inchangé : Voxtral p50/p95 601/951 ms, WLK 734/1 818 ms.
- ✓ Corpus `a` (un seul lecteur) avec `--speakers` : une seule identité sur 52 segments, pas de fragmentation.
- ⚠ Erreur résiduelle, structurelle : les premiers mots d'un nouveau tour, quand le segment fait moins de 2,5 s, partent avec la voix du tour précédent (« Call me Ishmael. » lu par la voix d'Alice). L'audio déjà joué ne se corrige pas ; réduire cette erreur demanderait de retarder la lecture, donc d'ajouter du délai.
- speechbrain revient dans les dépendances du mono (supersede partiellement ADR-0048 sur ce point) ; l'encodeur est chargé seulement avec `--speakers` (~80 Mo, téléchargé dans `pretrained_models/`, non versionné).
- ⚠ Pas de registre inter-sessions : les identités repartent de zéro à chaque session (alternative rejetée d'ADR-0048 toujours rejetée).
- ⚠ Six voix au plus avant réutilisation : au-delà de trois intervenants du même genre, deux identités partagent une voix.
- ⚠ Seuils calibrés sur trois lecteurs LibriVox d'un seul corpus : à revérifier sur des voix proches (même genre, même accent) et sur un vrai micro.
- Le chevauchement reste hors périmètre : deux voix simultanées donnent une empreinte qui ne ressemble à personne, gardée à l'identité courante. C'est le rôle du multi.

## Alternatives considérées

### Passer par le multi

Sortformer + séparation donnent aussi des identités, mais réintroduisent tout le coût qu'ADR-0048 a retiré (p95 bout en bout mesuré à 13,6 s avec séparation). Sans chevauchement, la séparation n'apporte rien.

### Diarisation Sortformer de WhisperLiveKit

Pas d'empreinte à calibrer, mais l'extra `diarization-sortformer` ramène NeMo dans le mono, il est incompatible avec la variante Voxtral (pas de diarisation côté serveur vLLM), et ses identifiants de locuteur ne sont pas stables d'un run à l'autre (ADR-0034). L'empreinte fonctionne à l'identique avec les deux STT.

### Une identité par segment, sans seuil de création plus élevé

Premier jet : un segment de ~1,6 s (« And what's the use of a book? ») sous le seuil créait une identité fantôme, de même qu'un segment à cheval sur un changement de tour (4 identités pour 3 lecteurs en simulation). D'où le seuil de création à 2,5 s et la réduction au plus long passage parlé.

### Voix choisie par ressemblance (clonage)

Indisponible avec le modèle publié, et la voix clonée depuis l'anglais dégrade fortement la synthèse française (ADR-0050).

## Révisions

- 2026-10-28 — création. `speaker_identity.py` (registre pur testé, `InputAudioHistory`, `SpeakerIdentifier`), `--speakers` dans `mono/main.py`, corpus `h` (`h_en_turns.wav`, tours dans `h_en_turns.turns.json`). Validé sur corpus `h` et `a` (cf. Conséquences).
