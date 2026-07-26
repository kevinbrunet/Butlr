# ADR 0046 — Profils de voix personnalisés par locuteur (T3.1-T3.3)

## Status

Accepted

## Context

Depuis ADR-0036, `main.py` synthétise tous les locuteurs avec une seule voix Pocket TTS de repli (`estelle`) — documenté comme limite connue ("T3.1-T3.3 pas commencés") depuis la mise en place du référentiel de locuteurs ouvert (ADR-0044). Deux locuteurs distincts sonnaient donc avec exactement la même voix, ce qui n'est plus acceptable une fois le suivi d'identité multi-locuteur en place.

Kevin a demandé : un pool de voix prédéfinies pour distinguer les locuteurs dès leur détection, puis un clonage vocal personnalisé construit progressivement à partir de l'audio source **propre** (sans chevauchement — l'audio séparé, potentiellement contaminé par un autre locuteur, ne doit jamais servir de base au clonage). Le profil doit être reconnu automatiquement d'une session à l'autre (pas seulement au sein d'un run) via les caractéristiques de la voix, et s'affiner à mesure que plus d'audio propre est accumulé pour ce locuteur — sans repère connu sur la durée optimale par palier de qualité.

✓ Vérifié cette session par lecture du README officiel (`github.com/kyutai-labs/pocket-tts`, lu le 2026-07-25) : `TTSModel.get_state_for_audio_prompt` accepte indifféremment un nom de voix prédéfini, un chemin de fichier, une URL `hf://`, un `.safetensors` déjà exporté, **ou un `torch.Tensor` en mémoire** — le clonage à partir d'un clip audio brut concaténé est donc directement supporté. `export_model_state(state, path)` / rechargement via `get_state_for_audio_prompt(path)` est le mécanisme de persistance déjà anticipé par ADR-0036, jamais implémenté jusqu'ici (confirmé par exploration du repo : zéro code, seulement des mentions ADR/docstring).

⚠ Aucune durée minimale/recommandée n'est documentée officiellement pour le clonage (ADR-0036 mentionne "~5s" sans source ferme). ⚠ Le clonage cross-lingue (voix source non-FR utilisée pour synthétiser du FR) n'est documenté nulle part comme fiable par Kyutai, et n'a jamais tourné dans Loom — risque accepté explicitement par Kevin pour le pool de repli (cf. Decision), à valider à l'oreille sur la machine cible.

Le suivi d'identité en direct (`speaker_tracking.py`, `known_embeddings` dans `main.py`, ADR-0042/0044) est entièrement en mémoire, scope à un seul run. Ce composant est le premier à persister quoi que ce soit entre deux runs.

## Decision

Deux nouveaux modules dans `loom_orchestrator/`, sur le modèle pur/impur déjà établi (`speaker_tracking.py` pur vs `speaker_separation.py` avec dépendances lourdes) :

- **`voice_registry.py`** (pur, testable sans Pocket TTS) : `VoiceTier` (palier de qualité, `NONE`/`LOW`/`MEDIUM`/`HD`, croissant avec la durée d'audio propre accumulée — seuils `TIER_LOW_S=8.0`/`TIER_MEDIUM_S=25.0`/`TIER_HD_S=60.0`, ⚠ non calibrés), `VoiceProfileRecord` (embedding + palier + durée + métadonnées), `find_matching_profile` (réutilise `speaker_tracking.cosine_similarity`/`MATCH_CONFIDENCE_THRESHOLD` — même seuil que le suivi d'identité en direct, pas de second seuil inventé), `VoiceRegistry` (persistance : `voice_profiles/manifest.json` + par locuteur `<speaker_key>.raw.wav` + `<speaker_key>.safetensors`, résolu relativement au fichier source, jamais au CWD).
- **`voice_personalization.py`** (impur) : `PersonalizedVoiceManager` — assigne une voix de pool à chaque nouvelle identité (`assign_fallback`, round-robin déterministe sur `FALLBACK_VOICE_POOL`), tente une correspondance dans le registre dès le premier embedding disponible pour une identité (`on_clean_audio`), accumule l'audio propre et reconstruit l'état vocal (`clone_voice_state`/`export_voice_state`) à chaque palier franchi. `get_voice_state(ident)` résout la voix à utiliser (personnalisée si construite, sinon pool, sinon `None` = repli du constructeur `PocketTtsSynthesizer`).

`FALLBACK_VOICE_POOL` = la liste complète des voix prédéfinies Pocket TTS (`estelle` en premier, seule confirmée FR, puis 21 voix EN + `giovanni`/`lola`/`juergen`/`rafael`) — Kevin a choisi le pool complet plutôt que `estelle` seule, pour maximiser la distinction entre locuteurs avant personnalisation, malgré le risque de qualité cross-lingue non vérifié.

"Affiner avec le temps" signifie ici *reconstruire l'état vocal à partir d'un prompt audio plus long* — Pocket TTS n'a pas d'API d'entraînement incrémental (prompt-conditioning uniquement). D'où la conservation du `.raw.wav` (pas seulement le `.safetensors` dérivé) : un locuteur reconnu dans une nouvelle session reprend son audio déjà accumulé plutôt que de repartir de zéro.

`tts_pocket.PocketTtsSynthesizer` reste un client fin : `synthesize_stream`/`commit_state._consume_stream` acceptent un `voice_state` optionnel (`None` = repli du constructeur), et de nouvelles méthodes primitives (`clone_voice_state`, `export_voice_state`, `load_voice_state`, `get_named_voice_state`) — il ne décide jamais lui-même quelle voix utiliser pour quel locuteur, c'est le rôle de `voice_personalization.py`.

Câblage limité à `main.py` (l'orchestrateur réel) pour cette passe — `bench/harness_pipeline.py`/`bench/harness_pipeline_dual.py` gardent la voix unique partagée, cohérent avec la non-fusion déjà actée entre `main.py` et les harnais (ADR-0044/0045).

Garde-fou mémoire GPU : `MAX_LOADED_PERSONALIZED_VOICES` (8) — au-delà, les nouvelles identités restent sur leur voix de pool, pas de construction/chargement d'état personnalisé tant qu'une place ne se libère pas. Pas d'éviction LRU en v1 (cf. Alternatives).

## Consequences

- Chaque locuteur détecté sonne distinctement dès sa première détection (voix de pool), puis avec sa propre voix clonée une fois assez d'audio propre accumulé — plus de confusion "tout le monde a la même voix" (limite connue depuis ADR-0044).
- ⚠ Qualité de synthèse FR non vérifiée pour les 24 presets de pool non-FR (clonage cross-lingue) — à valider à l'oreille sur la machine cible ; repli possible vers `estelle` seule si la qualité déçoit (changement de config, pas de code).
- ⚠ Seuils de palier (`TIER_LOW_S`/`TIER_MEDIUM_S`/`TIER_HD_S`) non calibrés — Kevin n'a pas de repère connu ; à ajuster après écoute réelle, cf. convention "mesurer avant d'optimiser".
- Le registre persiste de l'audio vocal identifiable de vraies personnes (`voice_profiles/.raw.wav`) — jamais commité (`.gitignore`), rétention/suppression long terme pas tranchée (hors scope POC).
- Pas d'éviction si `MAX_LOADED_PERSONALIZED_VOICES` est dépassé — un run avec plus de 8 locuteurs personnalisés simultanés dégrade silencieusement vers la voix de pool pour les identités en trop, sans erreur explicite au-delà du plafond lui-même.
- `bench/harness_pipeline.py`/`bench/harness_pipeline_dual.py` ne bénéficient pas de la personnalisation (scope limité à `main.py`) — à étendre plus tard si utile pour valider en isolation.
- `clone_voice_state` a nécessité deux itérations sur la machine cible pour trouver la bonne forme de tenseur (cf. Révisions) — ⚠ `export_voice_state`/`load_voice_state` restent non exercés par un run complet réussi au moment d'écrire ceci (aucun profil encore construit de bout en bout sans erreur).

## Alternatives considérées

- **Pool de repli `estelle` seule** : rejetée par Kevin — aucune distinction entre locuteurs tant qu'aucun n'est personnalisé, contrairement à la demande explicite d'un pool prédéfini assigné dès le départ.
- **Entraînement/fine-tuning incrémental du modèle de voix** : rejeté — l'API Pocket TTS ne l'expose pas (`get_state_for_audio_prompt` est un prompt-conditioning à chaque appel, pas un ajustement de poids). "Affiner" est donc implémenté comme une reconstruction depuis un prompt audio cumulatif plus long, pas un entraînement.
- **Éviction LRU des états personnalisés en mémoire dès v1** : rejetée pour cette passe — complexité prématurée avant de savoir si le plafond dur (`MAX_LOADED_PERSONALIZED_VOICES`) est jamais atteint en usage réel ; un plafond dur documenté est un garde-fou suffisant tant que ce n'est pas mesuré comme un problème concret (rappel : un état vocal jamais libéré a déjà causé une fuite VRAM de 13,9→29,85 Go dans ce projet, Révisions ADR-0042/0036 — d'où l'importance d'un garde-fou dès v1, même simple).
- **Ne persister que le `.safetensors` dérivé, pas le `.raw.wav`** : rejetée — sans l'audio brut, impossible de "reconstruire depuis un prompt plus long" lors d'une session ultérieure (Pocket TTS n'a pas d'API pour étendre un état déjà dérivé), ce qui casserait l'affinement inter-session demandé par Kevin. Coût de stockage jugé négligeable (~4 Mo par locuteur au palier HD).
- **Étendre `bench/harness_pipeline.py`/`harness_pipeline_dual.py` en même temps** : reporté — cohérent avec la non-fusion déjà actée entre `main.py` et les harnais (ADR-0044/0045), et `main.py` est le seul chemin qui compte pour un usage réel à ce stade.

## Révisions

- 2026-07-26 — création.
- 2026-07-26 — deux correctifs suite au premier run réel sur `corpus b` (fedora2) :
  1. ✓ `clone_voice_state` passait un tenseur audio 1D à `get_state_for_audio_prompt` ; le
     codec interne (`CompressionModel._encode_to_unquantized_latent`) attend `[batch, canal,
     temps]` (3D) — `AssertionError: expects audio of shape [B, C, T] but got
     torch.Size([1, 192000])`, capturée sans planter (aucun profil jamais construit).
     Corrigé (`.unsqueeze(0).unsqueeze(0)`) — ⚠ **corrigé une seconde fois ci-dessous, ce
     premier correctif était encore faux.**
  2. ✓ La première version dispatchait `on_clean_audio` via `asyncio.create_task` en
     parallèle du reste des appels GPU (traduction, synthèse, séparation) — a provoqué un
     segfault dur (exit 139, pas de traceback Python) sur la machine cible. Même classe de
     bug déjà rencontrée et documentée dans ce projet (ADR-0044 §Révisions, crash CUDA dans
     llama.cpp pour la même raison — paralléliser des appels GPU entre tâches). Corrigé :
     `on_clean_audio` passe maintenant par une file (`voice_personalization_queue`) drainée
     par `commit_worker`, le même consommateur unique qui sérialise déjà
     `translate`/`synthesize_stream` — priorité la plus basse (traité seulement quand aucun
     commit de traduction n'attend).
- 2026-07-26 — deux correctifs supplémentaires suite au second run réel sur `corpus b` :
  1. ✓ Le correctif précédent (`.unsqueeze(0).unsqueeze(0)`, 2 dims ajoutées) était encore
     faux : `AssertionError: ... got torch.Size([1, 1, 1, 192000])` (4D). Cause identifiée :
     `get_state_for_audio_prompt` ajoute **lui-même** la dimension batch en interne — il
     attend un tenseur `[canal, temps]` (2D) en entrée, pas `[temps]` (1D, premier essai, un
     seul dim ajouté en interne → 2D observé) ni `[batch, canal, temps]` (3D, second essai,
     un dim ajouté par-dessus → 4D observé). Un seul `unsqueeze(0)` (canal) est correct.
  2. ✓ L'audio accumulé par `route_window` est à 16kHz (`speaker_separation.SAMPLE_RATE_HZ`),
     jamais rééchantillonné vers la fréquence native de Pocket TTS (24kHz) avant clonage, et
     `voice_personalization.py` utilisait `PocketTtsSynthesizer.sample_rate_hz` (24kHz) pour
     calculer la durée d'audio accumulée — les deux bugs se combinaient pour faire franchir
     un palier ~1,5x trop tôt (192000 échantillons à 16kHz = 12s réelles, comptées comme 8,0s
     exactement, coïncidence qui a d'abord masqué le problème). Corrigé : `clone_voice_state`
     prend désormais `source_sample_rate_hz` en paramètre et rééchantillonne en interne
     (`torchaudio.functional.resample`) ; toute la durée/lecture/écriture WAV de
     `voice_personalization.py` utilise `speaker_separation.SAMPLE_RATE_HZ`, jamais la
     fréquence Pocket TTS.
