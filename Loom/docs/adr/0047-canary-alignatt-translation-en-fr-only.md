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

✓ Vérifié par documentation officielle NeMo (page "Canary Chunked and Streaming Decoding", lue le
2026-08-15) : contrairement à notre intégration Seamless (ADR-0041), qui ré-encode l'intégralité de
l'audio disponible à chaque poll (cause structurelle confirmée de l'OOM ayant motivé ADR-0043), NeMo
expose une politique de décodage streaming **native** pour Canary via `AEDStreamingDecodingConfig`
(`policy="alignatt"`), à contexte **borné** : `chunk_secs=2.0`, `left_context_secs=10.0`,
`right_context_secs=2.0`, `alignatt_thr=8`, `xatt_scores_layer=-2`. Le coût par étape ne grandit donc
a priori pas avec la durée totale de la ligne — la propriété précise qui manquait à notre intégration
Seamless. ⚠ Comportement exact du cache encodeur entre chunks non confirmé par exécution réelle,
seulement par lecture de doc — à vérifier au premier run.

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
