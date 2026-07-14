# ADR 0045 — Mixage de sortie au fil de l'eau et E/S audio via sounddevice

## Status

Accepted

## Context

`main.py` (T2.3) est resté un stub (`NotImplementedError`) tout au long du projet. Toute la
logique du pipeline (séparation PixIT, référentiel de locuteurs ouvert avec EMA, WLK par
identité, traduction LLM, TTS Pocket) a été construite et validée cette session dans le
harnais de benchmark `bench/harness_pipeline_dual.py`, en rejouant des fichiers WAV — jamais
sur une entrée audio réelle, jamais avec une sortie audio jouée en direct.

Kevin a demandé l'implémentation de l'orchestrateur réel, avec une exigence précise : mixer
les sorties TTS FR des différentes identités suivies en respectant leur timing relatif, pour
qu'un chevauchement réel entre deux locuteurs produise un vrai chevauchement audio en sortie
— pas une lecture séquentielle tour par tour. Deux décisions structurantes en découlent :
comment mixer plusieurs flux TTS en un seul flux de sortie synchronisé, et avec quelle
bibliothèque faire l'entrée/sortie audio (aucune n'est encore une dépendance de `env-loom/`).

`identity_timeline`/`_to_global_seconds` (`harness_pipeline_dual.py`, ADR-0044) donnent déjà,
pour chaque identité, l'ancrage temporel réel dans le flux source — ces deux mécanismes de
mixage possibles s'appuient sur les mêmes données, la question est seulement de savoir si on
les exploite pour un recalage explicite ou non.

## Decision

**Mixage "au fil de l'eau" (as-ready) pour cette v1** : chaque identité pousse son audio TTS
dans un tampon de sortie partagé dès qu'il est prêt, sans délai artificiel ni recalage
explicite sur `identity_timeline`. Si deux locuteurs se sont vraiment chevauchés dans la
source, leurs pipelines respectifs démarrent traduction+TTS à des moments proches en horloge
murale, et leur audio se chevauche naturellement dans le tampon partagé — sans jamais avoir
eu besoin de calculer un décalage exact entre les deux.

**`sounddevice` pour l'entrée microphone et la sortie mixée.**

## Consequences

- Le mixage devient possible sans ajouter de latence de sortie fixe — le budget bout-en-bout
  déjà tendu (p95 mesuré entre 1,8s et 9,3s selon les runs, cf. ADR-0044 §Révisions) n'est
  pas alourdi par un tampon de recalage.
- La fidélité au timing exact de chevauchement source n'est qu'opportuniste : si deux
  pipelines finissent à des instants trop éloignés (contention GPU différente d'une identité
  à l'autre, longueur de segment différente), le rendu peut être plus "tour par tour" que
  l'original ne l'était. Non mesuré à ce jour — premier test prévu sur `corpus b` en mode
  `--dry-run-wav`.
- `env-loom/pyproject.toml` gagne une nouvelle dépendance (`sounddevice>=0.4`), jamais
  installée/testée sur la machine cible avant ce chantier.
- `commit_worker` (ADR-0044) reste l'unique consommateur sérialisé des appels
  `LlmTranslator`/`PocketTtsSynthesizer` — le mixage de sortie ne parallélise pas la
  *génération* audio entre identités, seulement leur *lecture*, une fois déjà produite. Le
  risque de crash CUDA (`GGML_ASSERT(buffer) failed`) qui avait motivé cette sérialisation
  n'est pas réintroduit.
- Le canal dédié Bluetooth pour la voix de Kevin (§"Extension cible", ADR-0044) reste hors
  scope : la v1 gère N locuteurs sur un seul périphérique d'entrée partagé.
- Toutes les identités partagent la même voix Pocket TTS de repli (`estelle`, T3.1-T3.3 pas
  commencés) — deux locuteurs qui se chevauchent sonneront comme la même voix qui se parle
  dessus. Pas un défaut de ce mixage, mais à anticiper avant un premier test en direct pour
  ne pas le confondre avec un bug.

## Alternatives considérées

- **Recalage tamponné dès la v1** (introduire un délai de sortie fixe et replacer chaque
  identité à son offset relatif réel via `identity_timeline`/`_to_global_seconds`) : rejeté
  pour l'instant — reproduction plus fidèle du chevauchement d'origine, mais au prix d'une
  latence fixe supplémentaire pour tout le monde, tout le temps, sur un budget déjà tendu,
  pour corriger un défaut de fidélité jamais mesuré comme réellement gênant à l'oreille.
  Reste une extension bornée (les données nécessaires existent déjà) si un test sur la
  machine cible montre que le mixage au fil de l'eau ne suffit pas.
- **`pyaudio`** : rejeté — API plus bas niveau (pas de tableaux numpy natifs, `struct.pack`
  manuel), et aucun précédent dans ce dépôt. `sounddevice` est déjà une dépendance de
  `carson/` (même monorepo, `carson/pyproject.toml`, `carson/src/carson/main.py` utilise déjà
  `sd.query_devices()`/`asyncio.run_coroutine_threadsafe`) — précédent réel, pas un nouveau
  risque de compatibilité à évaluer à l'aveugle.
- **Factoriser `harness_pipeline_dual.py` et `main.py` en un module d'orchestration
  partagé** : rejeté pour l'instant — même raisonnement que la non-fusion déjà actée entre
  `harness_pipeline.py` et `harness_pipeline_dual.py` (ADR-0044) : l'architecture live n'a
  pas encore tourné une seule fois sur la machine cible, prématuré de la figer dans une
  abstraction partagée avant de savoir si elle tient la route telle quelle.

## Révisions

- 2026-07-19 — création.
