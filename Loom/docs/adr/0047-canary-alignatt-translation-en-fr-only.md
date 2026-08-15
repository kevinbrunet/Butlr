# ADR 0047 — Canary-1B-v2 + AlignAtt natif NeMo pour la traduction EN→FR, chinois mis en pause

## Status

Accepted

## Context

ADR-0043 a remplacé Seamless par un petit LLM local (Qwen3-4B-Instruct, `llama-cpp-python`) pour la
traduction, avec une politique de commit ponctuation/pause (`commit_policy.py`). Validé sur `corpus a`
(mono-locuteur, sans chevauchement) : qualité correcte, latence plate (cf. ADR-0043 Révisions
2026-07-16). Kevin juge le résultat global "plus que décevant" — pas satisfaisant en l'état actuel,
au-delà du seul goulot TTS déjà identifié (`Loom/CLAUDE.md`).

Recherche menée en réaction à ce retour (discussion en chat, 2026-08-15) sur les avancées récentes en
traduction simultanée de la parole. Piste retenue par Kevin : **Canary-1B-v2** (NVIDIA, NeMo,
CC-BY-4.0, poids publics sur Hugging Face) combiné à la politique **AlignAtt** (Papi et al., Interspeech
2023, arxiv 2305.11408 — déjà l'algorithme derrière ADR-0041/`alignatt.py`).

~ D'après la documentation officielle NeMo (page "Canary Chunked and Streaming Decoding", lue le
2026-08-15) : contrairement à notre intégration Seamless (ADR-0041), qui ré-encode l'intégralité de
l'audio disponible à chaque poll (cause structurelle confirmée de l'OOM ayant motivé ADR-0043), NeMo
expose une politique de décodage streaming **native** pour Canary via `AEDStreamingDecodingConfig`
(`streaming_policy="alignatt"`, `alignatt_thr=8`, `xatt_scores_layer=-2`). **Correction (2026-08-15,
cf. Révisions)** : la première version de ce paragraphe affirmait un contexte **borné**
(`chunk_secs`/`left_context_secs`/`right_context_secs`) comme argument central — introspection
directe de la dataclass sur la machine cible a montré que ces trois champs **n'existent pas** sur
`AEDStreamingDecodingConfig`. La doc décrivait probablement une fonctionnalité différente (chunking
pour l'inférence longue, `chunk_len_in_secs`), pas la politique de streaming elle-même. **Aucune
garantie de coût borné n'est donc confirmée** pour Canary à ce stade — la propriété qui manquait à
Seamless n'est pas démontrée résolue ici, seulement plausible (NeMo porte quand même la logique de
frontière nativement, cf. Decision/Consequences) et reste à mesurer empiriquement, pas à supposer.

⚠ Canary-1B-v2 ne couvre que 25 langues européennes (dont l'anglais et le français) — **le mandarin
n'est pas supporté** (vérifié sur la fiche HuggingFace `nvidia/canary-1b-v2`, lue le 2026-08-15).
Adopter Canary pour la traduction implique donc de sortir le chinois du périmètre actif de Loom, au
moins temporairement — décision explicite de Kevin ("abandonnons le chinois pour le moment"), pas un
oubli. Le petit LLM Qwen3-4B (ADR-0043) reste la seule voie qui couvre nominalement ZH→FR, même si sa
qualité sur le chinois n'a jamais été mesurée empiriquement (cf. ADR-0043 Consequences).

⚠ Risque supplémentaire trouvé en vérifiant l'API concrète (2026-08-15) : ticket GitHub
`NVIDIA-NeMo/NeMo#15231` (ouvert, non résolu, assigné mais sans réponse mainteneur au moment de la
lecture) rapporte que le décodage streaming AlignAtt (et Wait-K) sur `canary-1b-v2` **se bloque après
~20-40s** sur un fichier audio long (4 min) — le seuil AlignAtt n'est plus jamais atteint passé ce
point, transcription incomplète. C'est un scénario très proche de `corpus a` (mono-locuteur continu,
185s). La fonctionnalité streaming elle-même est récente : le ticket d'implémentation
`NVIDIA-NeMo/NeMo#14886` (Wait-K + AlignAtt pour Canary) est rattaché au milestone NeMo `25.11` — pas
un mécanisme mature et éprouvé, un ajout récent avec au moins un bug connu non résolu sur exactement
le cas d'usage qui nous intéresse (parole continue). Premier test à faire avant tout le reste :
vérifier si ce blocage se reproduit sur `corpus a`.

~ Une alternative plus proche de l'existant a été identifiée pendant la même recherche :
**AlignAtt4LLM** (IWSLT 2026, arxiv 2606.03967) adapte la politique AlignAtt à des LLM decoder-only
(pas de cross-attention encodeur-décodeur) via capture des poids d'attention à l'exécution — applicable
en théorie directement à notre Qwen3-4B déjà câblé et validé, sans ajouter de dépendance NeMo et sans
perdre le chinois. Non retenue pour cette itération (Kevin a explicitement demandé Canary), gardée en
alternative de repli si Canary échoue en pratique (cf. Alternatives).

## Decision

`nvidia/canary-1b-v2` (NeMo, chargé en process embarqué comme tout le reste de Loom, jamais un serveur
— cf. ADR-0039) devient un nouveau candidat de traduction **EN→FR uniquement**, évalué via la politique
de décodage streaming AlignAtt native de NeMo (`AEDStreamingDecodingConfig`), en isolation via un
nouveau harnais (`bench/harness_canary.py`, sur le modèle de `harness_seamless.py`/
`harness_llm_translate.py`) — **pas de câblage dans `harness_pipeline.py` avant mesure**, cf. convention
"mesurer avant d'optimiser" (`Loom/CLAUDE.md`).

Le chinois (ZH→FR) sort du périmètre actif de Loom pour la durée de cette expérimentation. Qwen3-4B/
`llama-cpp-python` (ADR-0043) reste tel quel, inchangé, et reste la seule voie qui couvre nominalement
le chinois si Kevin décide de le réactiver plus tard.

## Consequences

- ⚠ **Premier risque à invalider, avant tout le reste** : bug connu non résolu
  (`NVIDIA-NeMo/NeMo#15231`) de blocage du décodage streaming AlignAtt sur audio long (~20-40s) — à
  tester en priorité sur `corpus a` (185s). Si ça reproduit, ce chemin est bloqué tel quel (pas de
  contournement connu au moment de la rédaction) et l'alternative AlignAtt4LLM/Qwen3-4B (cf.
  Alternatives) devient le candidat par défaut.
- Moins de code à écrire/maintenir que l'intégration Seamless : pas besoin de ré-implémenter
  `alignatt.py` contre les tenseurs d'attention bruts de Canary — NeMo porte la logique de frontière
  nativement. `loom_orchestrator/alignatt.py` (pur, testé) n'est pas réutilisé sur ce chemin ; il reste
  en place pour Seamless (toujours présent en repli, ADR-0043).
- ✓ Correction par rapport à la première version de cette ADR : `nemo-toolkit[asr]` est **déjà** une
  dépendance de `env-loom` (`pyproject.toml`), installée et validée sur la machine cible depuis le
  2026-07-15 pour Sortformer (diarisation, ADR-0034) — pas une nouvelle dépendance à risque de conflit
  comme supposé initialement. ⚠ Source `git`, branche `main` (`[tool.uv.sources]`, pas de release PyPI
  stable au moment de l'install Sortformer) : rien ne garantit que le commit résolu le 2026-07-15
  inclut déjà le streaming AlignAtt pour Canary (mergé via `NVIDIA-NeMo/NeMo#14886`, rattaché au
  milestone `25.11`) — un `uv sync` pour rafraîchir `main` sera probablement nécessaire, à vérifier
  avant tout run.
- ⚠ Pas d'intégration `transformers`/HuggingFace native — NeMo (format `.nemo`) est un écosystème
  distinct des autres composants du pipeline (tous chargés via `transformers`/bibliothèques dédiées
  jusqu'ici). Nouvelle surface d'API à apprendre, pas de réutilisation de patterns existants.
- ⚠ Régression de portée assumée : le chinois sort du périmètre actif ("EN/ZH→FR" dans
  `Loom/CLAUDE.md` §Projet devient EN→FR pour la durée de cette expérimentation) — à documenter comme
  temporaire dans `Loom/CLAUDE.md`, pas comme un changement d'objectif définitif du POC.
- L'API Python exacte pour invoquer `AEDStreamingDecodingConfig`/`xatt_scores_layer` sur
  `canary-1b-v2` spécifiquement n'a été vérifiée que par documentation, jamais par lecture de code
  source NeMo ni par exécution — même statut que `speaker_separation.py` (ADR-0042) au moment de sa
  création : à confirmer au premier run réel, pas à supposer correct.
- Aucun changement sur les composants déjà validés (WLK/diarisation, Pocket TTS, registre de voix) —
  cette ADR ne touche que l'étage de traduction EN→FR.

## Alternatives considérées

- **AlignAtt4LLM sur Qwen3-4B déjà câblé** (arxiv 2606.03967) : politique de commit guidée par
  attention appliquée à notre LLM decoder-only existant, sans nouvelle dépendance NeMo, sans perdre le
  chinois. Pas retenue pour cette itération — Kevin a explicitement demandé Canary — mais c'est le
  repli à plus faible risque si Canary échoue en pratique (dépendances, qualité, ou latence).
- **Garder `commit_policy.py` (ponctuation/pause) sur Qwen3-4B sans rien changer** : rejeté — c'est
  l'état actuel jugé insatisfaisant par Kevin, ne répond pas à la demande.
- **Retour à Seamless+AlignAtt (SimulSeamless) tel quel** : rejeté à nouveau — ADR-0043 a déjà mesuré
  et documenté le défaut structurel (coût de ré-encodage croissant sans borne, OOM confirmé) ; rien de
  nouveau ne lève cette objection pour Seamless spécifiquement, contrairement à Canary dont le
  décodage natif est conçu à contexte borné.
- **Pipeline hybride dès maintenant (Canary pour EN, autre backend pour ZH, routage par langue
  source)** : rejeté pour l'instant — complexifierait l'expérimentation avant même d'avoir validé le
  premier backend. Le repli Qwen3-4B (qui couvre nominalement les deux langues) reste disponible sans
  routage explicite le temps de valider Canary isolément sur EN→FR.

## Révisions

- 2026-08-15 — création, suite à la demande explicite de Kevin d'essayer Canary-1B-v2 + AlignAtt et de
  mettre le chinois en pause. Pas encore implémenté ni mesuré sur la machine cible.
- 2026-08-15 — ⚠ conflit de dépendances confirmé sur fedora2 au premier `import nemo` après
  rafraîchissement de `nemo-toolkit` (`uv lock --upgrade-package nemo-toolkit`) : `asteroid`
  (tiré transitivement par `speechbrain`/`pyannote.audio[separation]`, ADR-0042/0044) impose
  `pytorch-lightning==1.4.9`, incompatible avec le NeMo `main` récent (`ImportError:
  cannot import name 'get_num_classes' from 'torchmetrics.utilities.data'`, module de compat
  legacy `pytorch_lightning.metrics`). Confirme la préoccupation initiale de cette ADR (dépendance
  neuve/instable) — pas sur l'axe attendu (nemo-toolkit lui-même), mais sur son interaction avec
  `asteroid`, déjà présent avant cette ADR. Corrigé par `[tool.uv] override-dependencies =
  ["pytorch-lightning>=2.0"]` dans `pyproject.toml` — force un PL récent pour tout le graphe. ⚠ Pas
  encore vérifié si ça casse `asteroid` lui-même ou la séparation de voix déjà validée (ADR-0042/
  0044) au runtime — à confirmer par `harness_separation.py` après ce changement, pas supposé sans
  danger juste parce que l'import de `nemo` réussit maintenant. Toujours pas de mesure sur
  `harness_canary.py` à ce stade.
- 2026-08-15 — modèle chargé avec succès sur fedora2 (dépendances débloquées), mais
  `AEDStreamingDecodingConfig(policy="alignatt", chunk_secs=..., left_context_secs=...,
  right_context_secs=...)` a échoué (`TypeError: unexpected keyword argument 'policy'`).
  Introspection directe de la dataclass (`dataclasses.fields`) sur la machine cible : le champ
  s'appelle `streaming_policy`, pas `policy` — et surtout, **`chunk_secs`/`left_context_secs`/
  `right_context_secs` n'existent pas du tout** sur cette classe (champs réels :
  `streaming_policy`, `alignatt_thr`, `waitk_lagging`, `exclude_sink_frames`,
  `xatt_scores_layer`, `max_tokens_per_alignatt_step`, `max_generation_length`,
  `use_avgpool_for_alignatt`, `hallucinations_detector`). **Corrige à la baisse la confiance du
  §Context** : l'argument "contexte borné, évite le défaut de Seamless" reposait sur une lecture
  de doc erronée — pas confirmé pour l'instant. `translation_canary.py` corrigé avec les noms
  réels. `translate()` (appel à `.transcribe()`) reste non exécuté.
- 2026-08-15 — premier run réel de `translate()` sur fedora2 (`corpus a`, `--chunk-s 999` — tout
  le fichier, ~185s, en un seul appel `.transcribe()`) : ✓ **le bug `NVIDIA-NeMo/NeMo#15231` ne
  se reproduit pas ici** — pas de blocage, latence mesurée 5,08s pour tout le fichier (`n=1`, cf.
  log). ⚠ Mais qualité inutilisable en l'état : sortie constatée par lecture directe du transcript
  — mélange anglais/français non traduit ("elle peeped into le book her sister was reading"), puis
  dégénérescence en boucle de répétition ("elle se trouvait sur la banque" ×20+) à partir d'un
  certain point, même mode de dégénérescence autoregressive déjà documenté pour Seamless (ADR-0040,
  corrigé à l'époque par `no_repeat_ngram_size`/`repetition_penalty` sur `generate()` — pas
  d'équivalent appliqué ici, aucun mécanisme de ce type identifié dans les champs de
  `AEDStreamingDecodingConfig`). Nettement en dessous de la qualité déjà validée pour Qwen3-4B sur
  ce même corpus (ADR-0043 : "correcte et cohérente"). ⚠ Cause pas isolée : ce test traduit 185s
  d'audio en un seul appel `.transcribe()` — plausible que le modèle dégénère spécifiquement sur
  un segment aussi long traité d'un bloc (jamais vu à l'entraînement), pas nécessairement sur des
  segments de taille normale. `strategy: beam`/`beam_size: 1` affiché dans les logs suggère aussi
  que `.transcribe()` fait un décodage beam classique sur tout l'audio encodé, pas forcément le
  chunk-par-chunk incrémental réel visé par la politique streaming — à confirmer. Prochain test :
  revenir à des segments de taille normale (`--chunk-s 10.0`, défaut) pour isoler "dégénère
  seulement sur audio très long en un bloc" de "qualité mauvaise en général".
- 2026-08-15 — run avec segments de taille normale (`--chunk-s 10.0`, défaut, 19 segments
  indépendants) : ✓ **plus de dégénérescence en boucle** — confirme que c'était spécifique au
  traitement de 185s en un seul appel, pas un défaut du modèle sur des segments réalistes.
  ✓ Latence bonne : p50=715,7ms/p95=934,8ms, 1 seul dépassement sur 19 (budget provisoire
  1000ms) — comparable à Qwen3-4B (ADR-0043, p50=214-487ms/p95=572-1170ms selon le run). ⚠
  Qualité lue par lecture directe du transcript : **code-switching systématique**, pas une erreur
  isolée — de l'anglais laissé tel quel dans la quasi-totalité des 19 segments ("Alice était
  beginning to get very excited", "Then elle looked at the sides of the well", "so she managed to
  put it into one of the cupboards"). Nettement en dessous de Qwen3-4B sur ce même corpus
  (ADR-0043 : "correcte et cohérente", une seule erreur isolée sur tout le fichier). Hypothèse non
  vérifiée : la politique AlignAtt force des commits précoces (faible latence) avant que le modèle
  ait vu assez de contexte pour décider de la traduction, et recopie le mot source faute de mieux
  — sous-traduction, un coût connu des politiques incrémentales agressives, pas nécessairement un
  défaut du modèle hors streaming. `use_streaming_policy=False` ajouté à
  `AlignAttCanaryTranslator`/`--no-streaming-policy` à `harness_canary.py` pour isoler cette
  hypothèse (décodage complet, sans contrainte de commit précoce) — pas encore testé. **Bilan
  intermédiaire** : à ce stade, Canary+AlignAtt streaming est moins bon que le repli Qwen3-4B déjà
  validé sur qualité, comparable en latence, sans le défaut de blocage `#15231` sur ce corpus. Le
  test `--no-streaming-policy` tranchera si c'est réparable (config de politique à ajuster) ou
  fondamental (pivoter vers AlignAtt4LLM/Qwen3-4B, cf. Alternatives).
- 2026-08-15 — run `--no-streaming-policy` (décodage par défaut, sans AlignAtt) sur `corpus a` :
  ✓ transcript **identique au caractère près** au run avec politique AlignAtt. Explication : le
  harnais donne le segment de 10s **déjà complet** à `.transcribe()` avant tout décodage — il n'y
  a donc jamais de "futur" audio que la politique streaming pourrait retenir, la contrainte
  qu'AlignAtt est censée imposer ne s'applique pas dans ce mode de test (limite du harnais, pas
  seulement du modèle — un vrai test du compromis latence/qualité demanderait de streamer l'audio
  chunk par chunk en dessous de la durée du segment, pas encore fait). **Conclusion tranchée** :
  le code-switching systématique n'est donc pas un artefact de la politique AlignAtt ni un réglage
  à corriger — c'est la qualité de traduction EN→FR de `canary-1b-v2` lui-même sur ce contenu,
  même à contexte complet, et elle est en dessous de Qwen3-4B déjà validé (ADR-0043). **Décision
  proposée à Kevin, pas encore tranchée** : abandonner cette piste pour la traduction et basculer
  sur l'alternative AlignAtt4LLM appliquée à Qwen3-4B (cf. Alternatives) plutôt que continuer à
  investiguer Canary — latence comparable au repli déjà validé, sans le gain de qualité espéré, et
  sans même avoir testé le vrai compromis streaming faute d'un harnais chunk-par-chunk.
