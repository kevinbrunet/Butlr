# ADR 0053 — Sortie : plus aucun audio jeté, retard rattrapé par coupe des silences puis accélération sans changement de hauteur

## Status

Accepted

## Context

ADR-0045 a posé le mixage de sortie au fil de l'eau avec un tampon par identité limité à 5 s, politique drop-oldest (`audio_mixer.DEFAULT_MAX_BUFFER_S`, ⚠ non calibré à l'époque). Le dépassement d'un tampon jetait donc le plus ancien audio synthétisé, sans trace audible autre qu'un trou.

Mesures du 2026-10-28 (mono, corpus `a`, WLK + Qwen3.5-9B + Pocket TTS) :

- ✓ Le français synthétisé dure plus longtemps que l'anglais source : ~15 s de retard accumulé en 3 min.
- ✓ La limite de 5 s jetait 18,6 s d'audio, dont des débuts de phrases. Mots perdus à la prononciation (sortie retranscrite par faster-whisper large-v3, comparée au français envoyé au TTS) : 19,7 % avec la limite, 4,3 % sans. Le TTS avait d'abord été soupçonné à tort ; un banc de remplacement du TTS a été arrêté après ce constat.
- ✓ En mode test (`DryRunWavSink`), la fermeture coupait aussi l'audio encore en attente en fin de source.

Kevin a tranché : tout garder, quitte à prendre du retard, puis le rattraper comme un interprète humain (raccourcis) — la sortie seule pour l'instant, en coupant les silences quand on est en retard, puis en accélérant la lecture s'il n'y a plus de silence à couper.

## Decision

1. **Plus aucune limite par défaut sur le tampon de sortie** (`DEFAULT_MAX_BUFFER_S = None`, mono et multi) ; une limite explicite réactive le drop-oldest. `DryRunWavSink.close()` joue tout ce qui reste avant de fermer.
2. **Rattrapage avant l'envoi au mixage** (`catch_up.py`, mono et multi, désactivable par `--no-catch-up`). Chaque incrément est entièrement synthétisé avant d'être poussé, on le retouche donc en entier sans ajouter de latence, selon le retard courant de son identité (`AudioMixer.pending_s`) :
   - retard > 0,5 s : silences de début et de fin ramenés à 0,1 s, pauses internes plafonnées à 0,25 s (détection par trames de 20 ms, -40 dB sous la trame la plus forte) ;
   - retard restant, déduction faite du silence coupé, > 1,5 s : accélération WSOLA (trames de 40 ms, hauteur conservée), linéaire jusqu'à ×1,25 atteint à 6 s de retard.

## Consequences

- ✓ Runs du 2026-10-28, un par variante, retard de la sortie à la fin de la source : mono Voxtral 3,4 s, mono WLK 5,6 s, multi Voxtral 4,2 s, multi WLK 3,3 s (contre ~15 s sans rattrapage en mono WLK). 12 à 29 s de silence coupées par run ; vitesse maximale atteinte ×1,19 à ×1,25. Prononciation mono : 3,7 % (Voxtral) et 4,4 % (WLK) de mots perdus, au niveau d'avant le rattrapage. En multi (corpus `b`, voix qui se chevauchent dans la sortie mixée), le chiffre de prononciation (43,6 et 63,1 %) mesure surtout le mélange des voix et ne dit rien du rattrapage.
- Bilan de fin de run imprimé (`CatchUp.summary`) : silence coupé, temps gagné par l'accélération, vitesse maximale.
- ⚠ Seuils non calibrés à l'écoute : premier jet. Une accélération à ×1,25 peut fatiguer l'auditeur, une coupe de pauses trop courte peut rendre le débit haché.
- ⚠ Au-delà de ce que ces deux étages rattrapent (débit source très soutenu, longue session), le retard grandit encore sans borne : il n'y a plus de perte, mais plus de borne non plus. Le rattrapage par la traduction (raccourcis, résumé quand on est en retard) reste à faire.
- La règle de `Loom/CLAUDE.md` « pas de file sans taille max et politique de drop documentée » ne s'applique plus au tampon de sortie : sa politique documentée est désormais « pas de drop, rattrapage en amont ».

## Alternatives considérées

### Garder le drop-oldest, avec une limite plus grande

Repousse le problème : toute limite finit par jeter du contenu sur une session longue, et l'auditeur perd des morceaux de phrases sans signal. Rejeté par Kevin (« on garde tout »).

### Accélérer la synthèse elle-même

⚠ Aucun réglage de débit dans l'API Pocket TTS utilisée (`synthesize_stream`), pas creusé plus loin. Un changement de débit à la génération rendrait aussi la latence du TTS dépendante du retard, alors que la retouche après synthèse n'en ajoute pas.

### Rééchantillonnage simple (lecture plus rapide)

Le plus simple, mais la hauteur monte avec la vitesse (effet « voix accélérée »), audible dès ~10 %. WSOLA garde la hauteur pour un coût CPU négligeable sur des incréments de quelques secondes.

### Raccourcir la traduction quand on est en retard

Ce que fait un interprète humain, et sans doute le levier le plus puissant, mais il touche au prompt et à la fidélité du contenu. Reporté à une seconde étape, à la demande de Kevin.

## Révisions

- 2026-10-28 — création. Supersede ADR-0045 sur la politique du tampon de sortie (le mixage au fil de l'eau reste inchangé).
