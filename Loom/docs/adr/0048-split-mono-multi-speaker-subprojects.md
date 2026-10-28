# ADR 0048 — Loom scindé en deux sous-projets : mono-locuteur (sans isolation de voix) et multi-locuteur

## Status

Accepted — partiellement supersedé par [ADR 0052](0052-mono-turn-taking-speaker-identification.md) (empreinte vocale de retour dans le mono, en option)

## Context

Jusqu'ici, Loom n'avait qu'un orchestrateur (`env-loom/src/loom_orchestrator/main.py`, ADR-0045), conçu pour N locuteurs sur un micro partagé. Il empile tout ce qu'il faut pour isoler une voix d'une autre :

- la séparation de voix (SepFormer ou pyannote, ADR-0042/0044) ;
- l'empreinte vocale ECAPA-TDNN et le suivi d'identité en référentiel ouvert (`speaker_tracking.py`) ;
- la diarisation Sortformer intégrée à WhisperLiveKit (ADR-0034) ;
- le routage par identité (`route_window`, un `AudioProcessor` par identité) ;
- le registre de voix inter-sessions, qui reconnaît un locuteur par son empreinte (ADR-0046).

Le cas d'un seul intervenant (conférencier, appel en tête-à-tête vu d'un côté) paie tout ce coût sans en tirer bénéfice : latence (✓ p95 bout-en-bout de 13,6 s mesuré avec séparation activée contre 1,5-2 s ciblés, cf. Révisions ADR-0042), VRAM (✓ 13 à 27 Go mesurés sur RTX 5090 selon l'étage), fragilité (✓ bugs de routage, de commit final et de parallélisation GPU consignés dans les Révisions d'ADR-0044 et 0046). Il masque aussi ce qu'on veut mesurer en mono : la qualité et la latence de la chaîne STT → traduction → TTS seule.

Kevin veut en parallèle comparer deux moteurs STT (WhisperLiveKit et Voxtral Realtime, ADR-0049) dans chacun des deux cas, soit quatre variantes.

## Decision

Loom est scindé en deux sous-projets autonomes, chacun avec son code et son environnement `uv` :

- `Loom/multi/` : le code existant, déplacé tel quel (`multi/env-loom/`), pour N locuteurs.
- `Loom/mono/` : une copie allégée pour un seul locuteur. Ni séparation, ni empreinte vocale, ni suivi d'identité, ni diarisation, ni routage, ni registre. Le flux audio va droit au STT. Le reste (commit partiel et final par ligne, traduction Qwen, Pocket TTS, mixage au fil de l'eau) est repris de multi.

Le mono garde le **clonage progressif** de la voix de sortie (`voice_cloning.py`) : voix de repli `estelle`, puis voix clonée reconstruite à chaque palier d'ADR-0046 (8, 25, 60 s), à partir de tout l'audio d'entrée, propre par construction puisqu'il n'y a qu'un locuteur. Sans empreinte vocale, il n'y a pas de registre inter-sessions.

Chaque sous-projet propose les deux variantes STT par une option `--stt {wlk,voxtral}` (ADR-0049). `Loom/corpus/` et `Loom/docs/adr/` restent communs.

## Consequences

- Le mono devient le banc de référence de la chaîne STT → traduction → TTS, sans les coûts ni les bugs du multi. Ses dépendances perdent speechbrain, pyannote.audio, NeMo (et l'extra `diarization-sortformer`), transformers et sentencepiece.
- Le code commun (traduction, TTS, commit, E/S audio, outillage de bench) est **dupliqué** entre les deux sous-projets, comme la non-fusion déjà actée entre `harness_pipeline.py` et `harness_pipeline_dual.py` (ADR-0044). Un correctif sur l'un doit être reporté à la main sur l'autre. C'est le prix de deux projets qui évoluent chacun à son rythme. ⚠ Aucun outil ne détecte une divergence involontaire ; à surveiller.
- Les versions de WhisperLiveKit, Pocket TTS, llama-cpp-python et torch du mono sont figées (`constraint-dependencies`) sur celles déjà validées dans `multi/env-loom/uv.lock`, pour comparer les deux sur la même base.
- Les chemins relatifs changent : `CORPUS_DIR` (`parents[5]`), le `license` des `pyproject.toml`, le volume corpus de `multi/docker-compose.yml`. Un `.venv` existant doit être resynchronisé (`uv sync`) après le déplacement.
- L'historique (harnais Seamless, Canary, AlignAtt, ADR-0040 à 0047) reste dans multi ; le mono ne reprend que ce que l'orchestrateur utilise.

## Alternatives considérées

### Un seul paquet, quatre points d'entrée

Modules communs partagés, deux orchestrateurs, le backend STT choisi par option. Pas de duplication. Rejeté par Kevin au profit de deux sous-projets autonomes : le mono doit pouvoir s'alléger (dépendances, code) sans que le multi le retienne, et inversement.

### Quatre dossiers (mono-wlk, mono-voxtral, multi-wlk, multi-voxtral)

Isolement maximal, mais duplication maximale. Les deux moteurs STT passent par la même interface (`process_audio`/`create_tasks`), donc les séparer en dossiers ne simplifie rien.

### Garder un seul orchestrateur et désactiver l'isolation par option

Un `--speakers 1` qui court-circuiterait la séparation. Rejeté : le chemin mono resterait encombré des dépendances et des structures du multi (sessions par identité, horloges par identité, files par identité), et ses mesures resteraient tributaires du code multi.

### Garder le registre inter-sessions en mono

Il exigerait de garder l'encodeur ECAPA uniquement pour reconnaître le locuteur d'une session à l'autre. Rejeté pour l'instant : l'objectif du mono est de retirer tout ce qui sert à isoler une voix. À rouvrir si la reprise d'une voix déjà clonée devient un besoin.

## Révisions

- 2026-10-26 — création. Code déplacé (`Loom/env-loom/` → `Loom/multi/env-loom/`) et sous-projet `Loom/mono/` créé.
- 2026-10-26 — premiers runs réels (fedora3, RTX 5090, `--stt wlk`, sortie en WAV) :
  - ✓ **Course à l'arrêt, mono puis multi** (segfault, exit 139, WAV de sortie perdu). `task_done()` était appelé avant le traitement, donc `join()` rendait la main pendant une traduction ou une synthèse en cours ; puis `cancel()` n'arrêtait pas le thread de `asyncio.to_thread`, et le commit final appelait llama.cpp/Pocket TTS en parallèle. Le consommateur unique s'arrête désormais de lui-même (`stop_requested`, files vides) et on l'attend. Défaut latent du multi depuis ADR-0045, corrigé dans les deux.
  - ✓ **Clonage affamé en mono** : servi seulement quand rien d'autre n'attendait, il n'a jamais eu son tour (file de commits jamais vide). L'audio s'accumule maintenant dans la boucle (sans GPU), et seule la reconstruction de voix, trois fois par session au plus, passe dans le consommateur, avant les commits partiels.
  - ✓ **Mono, corpus `a`, Pocket TTS 2.1.0** : bout en bout p50 1 315 ms, p95 2 092 ms ; STT p50 479 ms ; traduction p50 813 ms ; TTS (premier morceau) p50 483 ms. ⚠ Chiffres flattés par une voix clonée qui tronquait l'audio, cf. ADR-0050.
  - ✓ **Mises à jour** (demande de Kevin) : Pocket TTS 4.0.1 (ADR-0050), WhisperLiveKit 0.2.26, llama-cpp-python 0.3.36 (recompilé CUDA), torch 2.14.1, dans les deux sous-projets. En multi, la mise à jour complète (`uv lock --upgrade`) a été essayée puis abandonnée : NeMo `main`, transformers 5.20 et huggingface-hub 2.3. NeMo est désormais figé sur le commit du lock (71820fbc) au lieu de `main`, et ruff sur 0.15.21 (0.17 active de nouvelles règles par défaut).
  - ✓ **Diarisation Sortformer cassée en multi, défaut antérieur aux mises à jour** : WhisperLiveKit définit sa propre `StreamingSortformerState` sans `spkcache_compressed`, que NeMo 71820fbc lit déjà ; 19 `AttributeError` sur corpus `b`, STT ~5 s de retard. Corrigé par `compat.patch_wlk_sortformer_state()` (valeur de classe par défaut, comme NeMo). Multi, corpus `b`, après correctif : plus aucune exception, exit 0 ; bout en bout p50 7,3 s (problème de latence connu de la séparation, ADR-0042).
  - ✓ **Mono, corpus `a`, Pocket TTS 4.0.1, voix `estelle`, `--stt wlk`** : bout en bout p50/p95 1 588/7 285 ms, 17 incréments ; le TTS (CPU) produit l'audio à 0,81 fois le temps réel et le retard s'accumule. Variante Voxtral : cf. ADR-0049.
- 2026-10-26 — ✓ Après le TTS sur GPU (ADR-0051) : mono corpus `a`, `--stt wlk`, bout en bout p50/p95 758/1 321 ms, dans la cible (p95 1,5-2 s) ; `--stt voxtral` 546/724 ms (ADR-0049).
- 2026-10-28 — speechbrain (encodeur ECAPA seul) revient dans le mono pour l'option `--speakers` : identification des intervenants qui parlent à tour de rôle, une voix par identité, cf. [ADR 0052](0052-mono-turn-taking-speaker-identification.md). Supersede partiellement cet ADR sur ce point ; sans `--speakers`, le mono reste tel que décrit ici. Le registre inter-sessions reste rejeté.
