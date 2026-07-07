# CLAUDE.md — Loom

Sous-projet de [Butlr](../CLAUDE.md) — dont il hérite toutes les conventions (marqueurs de confiance, règles ADR, style de code). Ce fichier documente ce qui est spécifique à Loom. Les règles transverses au dépôt (`.claude/rules/`) s'appliquent ici sans exception.

## Projet

Loom = POC d'interprétariat simultané EN/ZH → FR. Audio d'un intervenant (anglais ou chinois) en entrée → audio français en sortie, une voix synthétique clonée par intervenant. Cible latence bout-en-bout p95 : 1,5-2s. Hardware de dev : RTX 5090 / Fedora. Cible future de dimensionnement (hors POC) : Raspberry Pi 5 + AI HAT+2.

**Stade actuel : T1.4 validé ; commit AlignAtt (ADR-0041) et séparation de voix + suivi par embedding (ADR-0042) implémentés dans `harness_pipeline.py`, aucun des deux pas encore validé sur la machine cible avec la séparation activée.** STT WLK confirmé fidèle sur corpus réel (2026-07-14). Traduction NLLB abandonnée (hallucinations récurrentes) au profit de SeamlessM4T v2, validé sur la machine cible (2026-07-15) — **mais Seamless montre à son tour une limite structurelle mesurée le 2026-07-15 sur `translate_partial` (coût temps + mémoire qui grandit sans borne avec la longueur de la ligne, cause des OOM CUDA sur les lignes longues, cf. ADR-0042) : décision prise (ADR-0043) de le remplacer par un petit modèle Qwen local (llama.cpp embarqué en process, pas de serveur) — pas encore implémenté, Seamless reste le code réel jusqu'à validation du remplacement.** Diarisation Sortformer/NeMo installée et validée sur un run à 2 locuteurs avec recouvrement (2026-07-15) : `speaker_id` stables, contamination de contenu résiduelle en zone de recouvrement (cf. ADR-0034) — c'est ce problème précis que traite ADR-0042. Politique de commit : une première approche ("attendre la fin de la ligne WLK") s'est révélée fausse pour un locuteur unique continu ; remplacée par AlignAtt (attention croisée du décodeur, déjà validé sur SeamlessM4T par SimulSeamless/FBK et déjà utilisé en interne par le STT de WLK) — `loom_orchestrator/alignatt.py` (pur, testé) + `translation_seamless.AlignAttSeamlessTranslator`. Séparation de voix : `loom_orchestrator/speaker_separation.py` (SepFormer-WHAMR + ECAPA-TDNN) + `speaker_tracking.py` (suivi d'identité par embedding, pur, testé) — approxime l'extraction ciblée faute de modèle mûr disponible (cf. ADR-0042). Aucun des deux n'a encore tourné sur la machine cible : forme exacte des tensors d'attention `transformers` (AlignAtt) et API SpeechBrain (séparation) toutes deux vérifiées par documentation seulement. Sonde TTS isolée (`bench/harness_tts.py`) : time-to-first-chunk dans le budget de 400ms sur des phrases courtes/moyennes, mais un warning `Maximum generation length reached without EOS` et un nombre de chunks très variable sur la même phrase suggèrent un problème de fin de génération à investiguer avant de faire confiance au composant. Voir le backlog complet transmis par Kevin (Phases 0-4) pour le détail des tâches.

## Documentation de référence

- `docs/adr/` — décisions d'architecture spécifiques à Loom, numérotées dans la séquence globale du dépôt (cf. `../.claude/rules/adr-writing.md`) :
  - `0033` — WhisperLiveKit pour STT streaming + diarisation + traduction intégrées (choix moteur — *superseded partiellement par 0039 sur l'aspect serveur*)
  - `0034` — Diarisation : Sortformer en primaire, diart en repli
  - `0035` — Traduction via NLLB intégré à WLK, pas de LLM (*superseded par 0040*)
  - `0036` — TTS : Pocket TTS avec voix clonées pré-exportées en `.safetensors`
  - `0037` — Orchestration en asyncio pur, sans Pipecat (*superseded par 0039 sur la topologie process*)
  - `0039` — Process unique, bibliothèques embarquées (pas de serveur, pas de WebSocket)
  - `0040` — SeamlessM4T v2 remplace NLLB pour la traduction, en 2 phases (batch puis streaming) (*superseded par 0043*)
  - `0041` — Politique de commit AlignAtt (attention Seamless), découplée des lignes WLK (*partie "commit côté Seamless" superseded par 0043 ; l'algorithme pur `alignatt.py` reste potentiellement réutilisable, cf. 0043*)
  - `0042` — Séparation de voix (SepFormer) + suivi d'identité par embedding, pour la contamination en zone de recouvrement — **ADR de référence pour la robustesse au chevauchement**
  - `0043` — Un petit LLM local (Qwen, llama.cpp embarqué) remplace Seamless pour la traduction — **ADR de référence pour la traduction actuelle, implémentation pas encore commencée**

## Architecture

```
Micro (16 kHz mono)
   │
   ▼
┌───────────────────────────────────────────────────────────┐
│ Process unique (asyncio)                                   │
│                                                              │
│  AudioProcessor (WhisperLiveKit, bibliothèque)              │
│   — STT streaming + diarisation (WLK ne traduit plus,       │
│     cf. ADR-0040 — traduction NLLB retirée)                 │
│   — process_audio() / create_tasks() : appel direct         │
│         │  segment audio complet par tour de parole         │
│         ▼                                                    │
│  SeamlessTranslator / AlignAttSeamlessTranslator             │
│  (SeamlessM4T v2, bibliothèque)                              │
│   — SeamlessM4Tv2ForSpeechToText, pas de vocoder             │
│   — Commit incrémental via AlignAtt (attention croisée du    │
│     décodeur, cf. ADR-0041) — pas d'attente de fin de tour   │
│         │                                                    │
│         ▼                                                    │
│  Orchestrateur (notre code) — increment AlignAtt → TTS,      │
│  registre voix, file bornée                                 │
│         │                                                    │
│         ▼                                                    │
│  TTSModel (Pocket TTS, bibliothèque)                         │
│   — generate_audio_stream(), embeddings .safetensors         │
│   — inférence déportée en executor (asyncio.to_thread)       │
└───────────────────────────────────────────────────────────┘
```

Pas de serveur, pas de WebSocket, pas de sérialisation entre les composants (cf. ADR-0039) : WhisperLiveKit, SeamlessM4T v2 et Pocket TTS sont importés comme bibliothèques dans le même process que l'orchestrateur. On ne code que la politique de commit (AlignAtt, cf. ADR-0041), le registre de voix et la glue asyncio — STT et diarisation restent délégués à WhisperLiveKit (cf. ADR-0033), la traduction à SeamlessM4T v2 (cf. ADR-0040, jamais son propre module Expressive — la sortie texte va à Pocket TTS).

## Stack

| Zone | Tech | Notes |
|---|---|---|
| STT + diarisation | WhisperLiveKit (bibliothèque, `TranscriptionEngine`/`AudioProcessor`) | Diarisation Sortformer installée et validée (T1.4, 2026-07-15 — `speaker_id` stables sur 2 locuteurs avec recouvrement, cf. ADR-0034), repli diart toujours disponible si besoin. Traduction NLLB **retirée** (ADR-0040) : `target_language` non utilisé. |
| Traduction | SeamlessM4T v2 (bibliothèque, `SeamlessM4Tv2ForSpeechToText`, `loom_orchestrator/translation_seamless.py`) | `SeamlessTranslator` : traduction complète (scellement final/filet de sécurité). `AlignAttSeamlessTranslator` (ADR-0041) : commit incrémental via attention croisée du décodeur (`generate(output_attentions=True)`), pas d'attente de fin de tour — ⚠ forme des tensors d'attention non vérifiée par exécution réelle. ~5,8 Go VRAM fp16. Codes langue ISO 639-3 (`eng`/`cmn`/`fra`) via `resolve_language_code`. |
| TTS | Pocket TTS (bibliothèque, `TTSModel`, `loom_orchestrator/tts_pocket.py`) | ✓ Constaté par exécution réelle (2026-07-15) : pas de variante FR plus légère que 24 couches — `french_24l` uniquement (`TTSModel.load_model(language="french")` échoue explicitement, cf. Révisions ADR-0036). ⚠ Pas encore de clonage par locuteur : `PocketTtsSynthesizer` utilise une unique voix FR de repli (`estelle`) en attendant T3.1-T3.3 ; voix clonées prévues en `.safetensors` (`export_model_state`/`import_model_state`). ✓ Le chunking Seamless (AlignAtt) et le chunking audio TTS sont découplés (2026-07-15, lecture de `pocket_tts/models/tts_model.py`) : `new_line_state()`/`synthesize_continuation()` réutilisent un même état vocal avec `copy_state=False` à travers tous les increments d'une ligne — l'audio s'enchaîne comme un seul énoncé continu, pas des extraits disjoints par increment de texte. |
| Séparation de voix | SpeechBrain (bibliothèque, `SepformerSeparation`/`EncoderClassifier`, `loom_orchestrator/speaker_separation.py` + `speaker_tracking.py`) | ADR-0042, pas encore exécutée sur la machine cible. `VoiceSeparator` (SepFormer-WHAMR, 8kHz — resampling 16kHz↔8kHz géré en interne) + `SpeakerEmbedder` (ECAPA-TDNN, 16kHz natif) approximent l'extraction ciblée (pas de modèle mûr disponible, cf. ADR-0042) : séparation aveugle + suivi d'identité par embedding (`speaker_tracking.py`, pur, testable) pour résoudre la permutation nous-mêmes. N'agit que sur une fenêtre bornée (`SEPARATION_WINDOW_S`, `harness_pipeline.py`), jamais une ligne WLK entière. |
| Orchestrateur | Python 3.12, asyncio, `uv` | **Un seul process** (cf. ADR-0039). Pas de framework de pipeline audio (cf. ADR-0037). Inférence TTS déportée en executor pour ne pas bloquer la boucle événementielle. |
| Environnement | `env-loom/` (venv unique) | Extras `diarization-sortformer` et `voxtral-hf` de WLK sont incompatibles entre eux — ne jamais les installer ensemble (cf. ADR-0034). |
| Déploiement dev | Docker Compose (1 service) | |

## Convention spécifique : mesurer avant d'optimiser

Aucun changement de config (modèle STT, backend diarisation, modèle TTS, `min_commit_words`...) sans benchmark avant/après sur le corpus de test (`corpus/`, cf. T0.2). Les budgets de latence par étage sont codés en dur dans l'instrumentation ; un dépassement est un WARNING loggué, jamais une variabilité silencieusement acceptée. Le budget "Transport" (100ms) devient une marge de sécurité plutôt qu'un coût réseau réel depuis ADR-0039 — pas de raison de le retirer du budget total sans mesure.

- `corpus/` — 6 wav 16kHz versionnés dans le repo (manifeste et provenance : `env-loom/src/loom_orchestrator/bench/corpus.py`). `e`/`f` (2026-07-15) : mêmes locuteurs/chevauchement que `b`, plus un bruit d'ambiance réel (DEMAND, CC BY-SA 3.0 — cafétéria/restaurant) mixé à un SNR cible de 10dB — pour tester la robustesse de la diarisation/STT sous bruit, pas seulement sous chevauchement.
- `env-loom/src/loom_orchestrator/bench/` — outillage de benchmark (T0.2-T0.4) : `replay.py` (injection temps réel, `send` générique — branché sur `AudioProcessor.process_audio`), `timestamps.py`/`clock.py`/`instrumentation.py` (log JSON lines par étage), `aggregate.py` (p50/p95), `line_tracking.py` (diff de texte par ligne WLK — `lines` n'est pas append-only, cf. ADR-0039), `harness.py` (commande WLK seul — `python -m loom_orchestrator.bench.harness <clé_corpus>`), `audio_chunks.py` (`iter_duration_chunks` : segments de N secondes pour `harness_seamless.py` ; `read_segment` : fenêtre arbitraire `[start_s, end_s)` pour extraire l'audio source d'un tour de parole, utilisé par `harness_pipeline.py`), `harness_seamless.py` (commande Seamless seul, bypass WLK/NLLB — `python -m loom_orchestrator.bench.harness_seamless <clé_corpus>`), `harness_pipeline.py` (câblage bout-en-bout WLK→AlignAtt→Seamless→Pocket TTS, commit incrémental par ligne WLK dès qu'AlignAtt juge un préfixe "sûr" — cf. ADR-0041, + séparation de voix/suivi par embedding avant traduction, cf. ADR-0042 — `python -m loom_orchestrator.bench.harness_pipeline <clé_corpus>` ; `--no-separation` pour comparer avec/sans ; écrit un seul wav FR par ligne dans `<run>-audio/` (`line<idx>.wav`, réécrit à chaque nouvel increment — chunking Seamless/TTS découplé, cf. Stack ci-dessus), en plus du transcript et du log de latences), `harness_tts.py` (sonde de latence Pocket TTS isolée, sans WLK ni Seamless, sur des phrases FR de longueurs variées répétées plusieurs fois — `python -m loom_orchestrator.bench.harness_tts` ; née du run T2.3-préliminaire du 2026-07-15 où le TTS mesurait p50=2993ms/p95=4340ms contre un budget de 400ms, pour savoir si c'est un coût fixe systématique ou un artefact du run bugué), `evaluate.py` (fonctions pures `diff_text`/`first_output_latency_s`, testables hors machine cible — `tests/bench/test_evaluate.py`) + `harness_evaluate.py` (CLI qui fait tourner `harness_pipeline.run_benchmark` sur tout ou partie du corpus et affiche, par clé : diff mot à mot FR vs référence manuscrite, latence lecture-wav-entrée→écriture-wav-sortie, chemins audio/transcript — `python -m loom_orchestrator.bench.harness_evaluate [clés...]`), `harness_separation.py` (sonde de latence séparation+embedding isolée, sans WLK/Seamless/TTS — `python -m loom_orchestrator.bench.harness_separation <clé_corpus>` ; née du premier run réel de ADR-0042, 2026-07-15, qui a montré une régression de latence sévère avec séparation activée — p95 bout-en-bout 13,6s contre 1,5-2s ciblés, cf. Révisions ADR-0042 — pour savoir si c'est le coût propre de SepFormer/ECAPA-TDNN ou la contention GPU de 5 modèles chargés simultanément). ⚠ `harness.py` ne mesure que l'étage WLK (STT) ; `harness_seamless.py` ne mesure que la traduction en isolation (chunks fixes via `--chunk-s`, pas de vrais tours de parole) ; `harness_pipeline.py` reste un harnais de validation séquentiel (pas de file bornée, pas de registre de voix — c'est le travail de l'orchestrateur final, T2.3, `main.py` reste `NotImplementedError`) ; `harness_tts.py` isole une seule inconnue (coût de `generate_audio` en fonction de la longueur du texte) ; `harness_evaluate.py` compare le texte concaténé de toutes les lignes, sans tenir compte des id de locuteur WLK (imprévisibles d'un run à l'autre) — repère les régressions de contenu, pas les erreurs d'attribution de locuteur (cf. ADR-0034 pour ça). Tous importent des bibliothèques GPU/CPU lourdes (`whisperlivekit`, `transformers`, `pocket_tts`, `speechbrain`) : non exécutables/testables hors de la machine cible.
- `loom_orchestrator/bench/reference_transcripts.py` — traductions FR de référence rédigées à la main (pas certifiées/publiées) pour `a`-`d` (`e`/`f` réutilisent celle de `b`), utilisées par `harness_evaluate.py`. Texte source = texte original connu (livre/passage standard, domaine public), pas une retranscription littérale de la sortie WLK — le diff capture donc à la fois la fidélité STT et la qualité de traduction.
- `loom_orchestrator/speaker_tracking.py` — algorithme de suivi d'identité par embedding pur (`cosine_similarity`, `pick_matching_stream`, `streams_are_distinct`, `update_running_embedding`, ADR-0042), sans dépendance `speechbrain`/`torch` — entièrement testable hors machine cible (`tests/test_speaker_tracking.py`). La lecture des modèles réels (SepFormer, ECAPA-TDNN) est isolée dans `speaker_separation.py`, pas ici — même séparation que `alignatt.py`/`translation_seamless.py`.
- `loom_orchestrator/speaker_separation.py` — `VoiceSeparator` (SepFormer-WHAMR) et `SpeakerEmbedder` (ECAPA-TDNN), composants du pipeline (ADR-0042), pas des outils de mesure. ⚠ API vérifiée par documentation officielle SpeechBrain seulement, pas par exécution réelle (pas la machine cible).
- `loom_orchestrator/alignatt.py` — algorithme AlignAtt pur (`safe_token_count`, `compute_increment`, ADR-0041), sans dépendance `transformers`/`torch` — entièrement testable hors machine cible (`tests/test_alignatt.py`). La lecture des tensors d'attention réels est isolée dans `translation_seamless.AlignAttSeamlessTranslator`, pas ici.
- `loom_orchestrator/translation_seamless.py` — `SeamlessTranslator` (traduction complète) et `AlignAttSeamlessTranslator` (commit incrémental, ADR-0041), hors du dossier `bench/` : composants du pipeline (pas des outils de mesure), appelés par `harness_pipeline.py` et, plus tard, par l'orchestrateur final.
- `loom_orchestrator/tts_pocket.py` — `PocketTtsSynthesizer`, même statut que `translation_seamless.py` : composant du pipeline, pas un outil de mesure. ✓ `language="french_24l"` confirmé par exécution réelle (pas d'alternative FR plus légère, cf. Révisions ADR-0036). ⚠ Le nom court de voix `"estelle"` reste vérifié par documentation seulement, pas par exécution ; pas de clonage par locuteur (voix de repli unique).

## Ce qu'il NE faut PAS faire (spécifique à Loom, en plus des règles Butlr)

- ⚠ Ne pas traiter un fichier wav brut sur le chemin critique de synthèse TTS — uniquement des embeddings `.safetensors` pré-exportés (cf. ADR-0036).
- ⚠ Ne pas laisser une file interne (WLK→orchestrateur, orchestrateur→TTS) sans taille max et politique de drop documentée dans le code.
- ⚠ Ne pas ré-émettre ou corriger de l'audio déjà joué — le passé est immuable, les révisions STT ne s'appliquent qu'au futur.
- ⚠ Ne jamais faire remonter de backpressure vers WLK (diarisation en retard = label appliqué aux groupes suivants ; TTS en retard = dégradation contrôlée, jamais de blocage amont).
- ⚠ Ne pas installer les extras `diarization-sortformer` et `voxtral-hf` de WhisperLiveKit dans le même environnement (cf. ADR-0034).
- ⚠ Ne pas lancer WhisperLiveKit ou Pocket TTS en mode serveur (CLI `whisperlivekit-server`, `pocket-tts serve` ou équivalent) — usage bibliothèque uniquement (cf. ADR-0039). Si un serveur redevient nécessaire un jour (ex. accès distant), ça se rediscute en ADR, pas en silence.
- ⚠ Ne pas laisser l'inférence Pocket TTS (bloquante, CPU) tourner directement dans la boucle asyncio principale — la déporter en executor pour ne pas geler le traitement WLK en cours (cf. ADR-0039).
- ⚠ Ne pas utiliser le module Expressive/vocoder de Seamless (synthèse vocale) — seule la sortie texte de `SeamlessM4Tv2ForSpeechToText` est utilisée, Pocket TTS reste seul responsable de la synthèse et du clonage de voix par locuteur (cf. ADR-0040).
- ⚠ Ne pas réintroduire NLLB/`nllw` (le sous-package de traduction de WLK) — confirmé peu fiable en streaming (ADR-0040). `target_language` de `TranscriptionEngine` ne doit plus être configuré.
- ⚠ Ne pas faire tourner le LLM de traduction (ADR-0043) via un `llama-server` distant (le pattern déjà utilisé pour carson, cf. `scripts/README.md`) — violerait ADR-0039 pour Loom spécifiquement (process unique, pas de serveur) et ajouterait un aller-retour réseau incompatible avec la cible de latence. Bindings Python de llama.cpp embarqués en process uniquement.
