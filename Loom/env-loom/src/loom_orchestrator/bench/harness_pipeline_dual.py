from __future__ import annotations

import argparse
import asyncio
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (cf. harness_pipeline.py).
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    format_report,
    load_events,
)
from loom_orchestrator.bench.harness_pipeline import (
    LineCommitState,
    _consume_continuation,
    _release_gpu_state,
    _write_wav,
)
from loom_orchestrator.bench.instrumentation import (
    STAGE_TRANSLATE_LLM,
    STAGE_TTS,
    STAGE_WLK,
    EventLogger,
    LatencyEvent,
)
from loom_orchestrator.bench.line_tracking import extract_updates
from loom_orchestrator.bench.replay import replay_realtime
from loom_orchestrator.bench.timestamps import hms_to_seconds
from loom_orchestrator.commit_policy import compute_flush, force_flush
from loom_orchestrator.speaker_separation import (
    PYANNOTE_CHUNK_SAMPLES,
    SAMPLE_RATE_HZ,
    PyannoteVoiceSeparator,
    SpeakerEmbedder,
    VoiceSeparator,
)
from loom_orchestrator.speaker_tracking import (
    assign_and_bootstrap,
    cosine_similarity,
    is_confident_match,
    pick_active_identity,
    streams_are_distinct,
    update_running_embedding,
)
from loom_orchestrator.translation_llm import LlmTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# Câblage bout-en-bout avec séparation en amont de WLK (ADR-0044) : contrairement à
# harness_pipeline.py (`--translator llm`), où l'audio mélangé va directement à un seul WLK,
# ici l'audio brut est séparé en continu AVANT WLK et routé vers l'AudioProcessor du
# locuteur suivi correspondant — un seul TranscriptionEngine partagé (poids chargés une
# fois, cf. ADR-0044 §Context), N_IDENTITIES AudioProcessor indépendants. Chaque processor
# traite ensuite sa ligne exactement comme le chemin "llm" de harness_pipeline.py
# (commit_policy + LlmTranslator + PocketTtsSynthesizer) — cette partie est dupliquée ici
# plutôt que factorisée avec harness_pipeline.py pour l'instant (mesurer d'abord si cette
# architecture tient la route avant de fusionner le code commun, cf. "pas d'abstraction
# prématurée", Loom/CLAUDE.md hérite des règles racine).
#
# ⚠ Premier jet (ADR-0044), pas encore validé sur la machine cible. Couvre uniquement
# translator="llm" (ADR-0043) — la séparation en aval (ADR-0042) reste le mécanisme du
# chemin "seamless", inchangé, cf. harness_pipeline.py.

N_IDENTITIES = 2

# Mêmes valeurs que harness_pipeline.py/ADR-0042 pour la fenêtre de séparation (zone de
# confort mesurée de SepFormer-WHAMR). ROUTE_EVERY_S est nouveau ici : contrairement à
# ADR-0042 (séparation ponctuelle juste avant un commit de traduction), la séparation
# tourne en continu — ROUTE_EVERY_S throttle la fréquence des appels, pas leur fenêtre.
# ⚠ Pas calibré empiriquement : implique qu'aucun audio ne peut atteindre WLK avant que
# SEPARATION_WINDOW_S se soit accumulé (latence de démarrage de ligne non mesurée) — à
# surveiller en premier sur un vrai run (cf. ADR-0044 Conséquences, pas encore documentée
# avant ce premier jet de code).
SEPARATION_WINDOW_S = 6.0
ROUTE_EVERY_S = 1.0

# ⚠ Pas calibré empiriquement — même idée que MIN_SEPARATION_AUDIO_S dans harness_pipeline.py
# (ADR-0042) : en dessous de ce seuil de contexte accumulé, SepFormer n'a pas assez de matière
# pour séparer utilement (proposition de Kevin, 2026-07-17) — route_window saute l'appel à
# separator.separate() et route l'audio brut directement (même chemin que "un seul locuteur
# actif"), le temps que la fenêtre de séparation atteigne une taille utile.
MIN_SEPARATION_AUDIO_S = 2.0

# ⚠ Pas calibré empiriquement (cf. Révisions ADR-0044, corrigé après le premier run réel qui
# a montré un pacing temps réel désynchronisé faute de découplage feed()/route_consumer()) —
# taille de départ, quelques secondes de retard tolérées avant de perdre de l'audio.
ROUTE_QUEUE_MAXSIZE = 5


@dataclass
class PipelineDualBenchmarkResult:
    log_path: Path
    transcript_path: Path
    audio_dir: Path


@dataclass
class IdentitySession:
    """État par identité de locuteur suivie (ADR-0044) — équivalent du dict `commit_state`
    de harness_pipeline.py, mais tenu par identité plutôt que par ligne WLK d'un flux
    unique."""

    processor: AudioProcessor
    commit_state: dict[int, LineCommitState] = field(default_factory=dict)
    known_texts: list[str] = field(default_factory=list)
    sealed: set[int] = field(default_factory=set)
    last_lines: list[dict] = field(default_factory=list)


def _pcm16_bytes(audio: "np.ndarray") -> bytes:
    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    return pcm16.tobytes()


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    lan: str = "auto",
    target_lang: str = "fr",
    separator_backend: str = "sepformer",
) -> PipelineDualBenchmarkResult:
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)
    source_lang = next(c for c in corpus.CORPUS_MANIFEST if c.key == corpus_key).language

    run_id = f"{corpus_key}-dual-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    audio_dir = out_dir / f"{run_id}-audio"
    out_dir.mkdir(parents=True, exist_ok=True)
    audio_dir.mkdir(parents=True, exist_ok=True)
    transcript_path.write_text("", encoding="utf-8")

    # ⚠ Sortformer (NeMo) désactivé quand separator_backend="pyannote" — constaté (2026-07-17,
    # cf. Révisions ADR-0044) : pyannote.audio[separation] force une résolution de dépendances
    # (pytorch-lightning très ancien, imposé par nemo-toolkit) qui casse l'initialisation de
    # Sortformer (échec silencieux, `sys.exit(1)` côté NeMo, pas d'exception attrapable). Pas
    # une perte fonctionnelle sur ce chemin : `pyannote/separation-ami-1.0` retourne déjà sa
    # propre diarisation avec la séparation (`diarization, sources = model(...)`), Sortformer
    # ferait double emploi. Chaque session WLK ne voit de toute façon qu'un flux déjà séparé
    # par identité — sa propre diarisation servirait de filet de sécurité, pas de mécanisme
    # principal (même raisonnement que pour SepFormer, cf. Decision plus haut).
    use_sortformer = separator_backend != "pyannote"
    engine_kwargs = {"pcm_input": True, "diarization": use_sortformer, "lan": lan}
    if use_sortformer:
        engine_kwargs["diarization_backend"] = "sortformer"
    engine = TranscriptionEngine(**engine_kwargs)
    sessions = [
        IdentitySession(processor=AudioProcessor(transcription_engine=engine, mode="full"))
        for _ in range(N_IDENTITIES)
    ]
    # ⚠ pyannote/separation-ami-1.0 impose une fenêtre fixe de 5s (PYANNOTE_CHUNK_SAMPLES) —
    # separation_window_s remplace localement le module-level SEPARATION_WINDOW_S (6s, réglé
    # pour SepFormer) pour ce backend, cf. Révisions ADR-0044 (2026-07-17) : premier test
    # audible seulement une fois la fenêtre correctement dimensionnée/positionnée.
    if separator_backend == "pyannote":
        separator = PyannoteVoiceSeparator()
        separation_window_s = PYANNOTE_CHUNK_SAMPLES / SAMPLE_RATE_HZ
    elif separator_backend == "sepformer":
        separator = VoiceSeparator()
        separation_window_s = SEPARATION_WINDOW_S
    else:
        raise ValueError(
            f"separator_backend inconnu : {separator_backend!r} — attendu 'sepformer' ou "
            "'pyannote'"
        )
    embedder = SpeakerEmbedder()
    llm_translator = LlmTranslator()
    synth = PocketTtsSynthesizer()

    known_embeddings: list[list[float] | None] = [None] * N_IDENTITIES
    embedding_counts: list[int] = [0] * N_IDENTITIES

    # ⚠ Identifié seulement après un run réel qui restait à p95=9s malgré le découplage
    # ci-dessus (cf. Révisions ADR-0044) : `AudioProcessor.process_audio` ne reçoit que des
    # octets PCM bruts, sans horodatage — WLK ne peut donc mesurer `line["end"]` que relatif
    # au volume d'audio *reçu par cette session*, jamais à la position réelle dans le flux
    # source global. Or chaque identité ne reçoit qu'une fraction discontinue de l'audio
    # (silence pendant que l'autre locuteur parle) : son horloge interne prend du retard sur
    # l'horloge murale réelle, un retard qui grandit avec la part de temps de parole de
    # l'autre identité — mesurer la latence WLK en comparant `line["end"]` (horloge interne
    # du processor) à l'horloge murale globale confond donc ce décalage structurel avec de la
    # vraie latence. `identity_timeline[ident]` : liste de `(échantillon_début_processor,
    # échantillon_fin_processor, échantillon_début_global)` par incrément envoyé — permet de
    # retraduire un timestamp interne à un processor vers sa position réelle dans le flux
    # source global avant de mesurer quoi que ce soit.
    identity_timeline: list[list[tuple[int, int, int]]] = [[] for _ in range(N_IDENTITIES)]

    # DEBUG (diagnostic ADR-0044, à retirer une fois la cause de l'écart id0/id1 confirmée) :
    # volume total réel vs silence envoyé par identité, et compte des décisions de routage
    # distinct/non-distinct — pour savoir si l'écart de latence vient de la branche silence
    # (peu de contenu réel) ou de la branche "chevauchement" (contenu réel des deux côtés).
    content_seconds = [0.0] * N_IDENTITIES
    silence_seconds = [0.0] * N_IDENTITIES
    distinct_count = [0]
    non_distinct_count = [0]
    too_short_count = [0]

    def _record_send(
        ident: int, n_samples: int, global_start_sample: int, is_silence: bool = False
    ) -> None:
        timeline = identity_timeline[ident]
        processor_start = timeline[-1][1] if timeline else 0
        timeline.append((processor_start, processor_start + n_samples, global_start_sample))
        if is_silence:
            silence_seconds[ident] += n_samples / SAMPLE_RATE_HZ
        else:
            content_seconds[ident] += n_samples / SAMPLE_RATE_HZ

    def _to_global_seconds(ident: int, processor_seconds: float) -> float:
        processor_sample = int(processor_seconds * SAMPLE_RATE_HZ)
        for processor_start, processor_end, global_start in identity_timeline[ident]:
            if processor_start <= processor_sample <= processor_end:
                return (global_start + (processor_sample - processor_start)) / SAMPLE_RATE_HZ
        # Pas encore de correspondance connue (ne devrait pas arriver en usage normal, cf.
        # docstring ci-dessus) — retourne tel quel plutôt que de faire échouer une mesure.
        return processor_seconds

    with EventLogger(log_path) as logger:
        replay_start_monotonic = time.monotonic()

        async def emit_increment(
            ident: int, idx: int, speaker: str, increment: str, event_stage_t_in: float
        ) -> None:
            if not increment:
                return
            state = sessions[ident].commit_state[idx]
            if state.voice_state is None:
                state.voice_state = synth.new_line_state()

            # ⚠ Pas de lock ici : LlmTranslator/PocketTtsSynthesizer sont partagés entre les
            # N_IDENTITIES sessions, mais `commit_worker` (cf. plus bas) est l'unique tâche
            # qui les appelle — la sérialisation vient de la structure (un seul consommateur),
            # pas d'un verrou explicite. Historique : un premier jet appelait ceci directement
            # depuis `consume()` (une tâche par identité, donc 2 appelantes concurrentes) et a
            # provoqué un crash CUDA dur dans llama.cpp (GGML_ASSERT(buffer) failed) — corrigé
            # une première fois avec des `asyncio.Lock`, puis remplacé par ce découplage qui
            # résout aussi le vrai problème sous-jacent (cf. docstring de `commit_worker`).
            new_chunks, ttfc_s = await asyncio.to_thread(
                _consume_continuation, synth, state.voice_state, increment
            )
            t_first_chunk = event_stage_t_in + ttfc_s
            segment_id = f"{corpus_key}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TTS, event_stage_t_in, t_first_chunk))

            state.audio_chunks.extend(new_chunks)
            import numpy as np

            full_audio = np.concatenate(state.audio_chunks)
            _write_wav(audio_dir / f"id{ident}-line{idx}.wav", full_audio, synth.sample_rate_hz)

            with transcript_path.open("a", encoding="utf-8") as f:
                f.write(f"[id{ident}/{speaker}] FR (increment) : {increment}\n")
            state.chunk_count += 1

        async def try_llm_commit(ident: int, idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            if not text or end is None:
                return
            session = sessions[ident]
            state = session.commit_state.setdefault(idx, LineCommitState())
            segment, new_flushed, is_consistent = compute_flush(text, state.flushed_source)
            if not is_consistent:
                print(
                    f"WARNING id{ident}: WLK a révisé du texte déjà flushé sur line{idx} "
                    f"(source={text!r}, déjà flushé={state.flushed_source!r}). Ignoré."
                )
                return
            state.flushed_source = new_flushed
            if not segment:
                return

            end_s = _to_global_seconds(ident, hms_to_seconds(end))
            translated = await asyncio.to_thread(
                llm_translator.translate, segment, source_lang, target_lang
            )
            t_translate_end = time.monotonic() - replay_start_monotonic
            segment_id = f"{corpus_key}-id{ident}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end))

            state.committed_fr = f"{state.committed_fr} {translated}".strip()
            speaker = line.get("speaker", "?")
            await emit_increment(ident, idx, speaker, translated, t_translate_end)

        async def force_final_commit_llm(ident: int, idx: int, line: dict) -> None:
            text, end = line.get("text"), line.get("end")
            session = sessions[ident]
            state = session.commit_state.setdefault(idx, LineCommitState())
            if text and end is not None:
                segment, new_flushed, is_consistent = force_flush(text, state.flushed_source)
                if not is_consistent:
                    print(
                        f"WARNING id{ident}: traduction finale de line{idx} incohérente "
                        f"(déjà flushé={state.flushed_source!r}, source={text!r}). Ignoré."
                    )
                else:
                    state.flushed_source = new_flushed
                    if segment:
                        end_s = _to_global_seconds(ident, hms_to_seconds(end))
                        translated = await asyncio.to_thread(
                            llm_translator.translate, segment, source_lang, target_lang
                        )
                        t_translate_end = time.monotonic() - replay_start_monotonic
                        segment_id = f"{corpus_key}-id{ident}-line{idx}-final"
                        logger.log(
                            LatencyEvent.create(
                                segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end
                            )
                        )
                        state.committed_fr = f"{state.committed_fr} {translated}".strip()
                        speaker = line.get("speaker", "?")
                        await emit_increment(ident, idx, speaker, translated, t_translate_end)
            _release_gpu_state(state)

        # Une seule tâche de fond (commit_worker, plus bas) traite try_llm_commit/
        # force_final_commit_llm — jamais `consume()` directement. `partial_queues[ident]`
        # (maxsize=1, "la dernière valeur gagne") pour la ligne active d'une identité :
        # coalescer plutôt que mettre en file toutes les mises à jour WLK intermédiaires,
        # puisque `compute_flush` n'a besoin que du texte le plus récent (les anciennes
        # captures sont des préfixes de la nouvelle, cf. "le passé est immuable"). `final_queue`
        # (illimitée en pratique — bornée par le nombre total de lignes d'un run, jamais
        # coalescée : chaque scellement de ligne doit être traité, aucun n'est remplaçable).
        partial_queues: list[asyncio.Queue] = [asyncio.Queue(maxsize=1) for _ in sessions]
        final_queue: asyncio.Queue = asyncio.Queue()

        def _queue_latest(queue: asyncio.Queue, item: tuple) -> None:
            if queue.full():
                try:
                    queue.get_nowait()
                    queue.task_done()
                except asyncio.QueueEmpty:
                    pass
            queue.put_nowait(item)

        async def commit_worker() -> None:
            """Tâche de fond unique qui appelle try_llm_commit/force_final_commit_llm — cf.
            Révisions ADR-0044 : un premier jet appelait ces fonctions directement depuis
            `consume(ident)`, dans la même boucle qui lit les résultats WLK. Une fois
            LlmTranslator/PocketTtsSynthesizer partagés entre 2 identités réellement
            concurrentes, chaque appel bloquant à `translate`/`synthesize_continuation`
            retardait d'autant la lecture du résultat WLK suivant — exactement la
            "backpressure vers WLK" interdite par `Loom/CLAUDE.md`, mesurée comme une
            explosion de la latence de l'étage `wlk` alors que WLK lui-même (et le routage
            audio en amont, `route_window`) n'y étaient pour rien. Ce worker unique découple
            `consume()` (jamais bloqué au-delà de la lecture WLK) du travail GPU réel, et
            sérialise `translate`/`synthesize_continuation` par construction (un seul
            appelant) — plus besoin des `asyncio.Lock` du premier correctif.
            """
            while True:
                if not final_queue.empty():
                    ident, idx, line = await final_queue.get()
                    try:
                        await force_final_commit_llm(ident, idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit final
                        print(f"WARNING: force_final_commit_llm(id{ident}) a échoué ({exc!r}).")
                    finally:
                        final_queue.task_done()
                    continue

                did_work = False
                for ident, queue in enumerate(partial_queues):
                    if queue.empty():
                        continue
                    idx, line = queue.get_nowait()
                    queue.task_done()
                    did_work = True
                    try:
                        await try_llm_commit(ident, idx, line)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:  # noqa: BLE001 — isole une erreur de commit partiel
                        print(f"WARNING: try_llm_commit(id{ident}) a échoué ({exc!r}).")
                if not did_work:
                    await asyncio.sleep(0.02)

        async def consume(ident: int) -> None:
            session = sessions[ident]
            results_generator = await session.processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])
                session.last_lines = lines

                for idx, line, _text in extract_updates(lines, session.known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    segment_id = f"{corpus_key}-id{ident}-wlk-line{idx}-{len(_text)}"
                    t_in = _to_global_seconds(ident, hms_to_seconds(end))
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                if not lines:
                    continue

                for idx in range(len(lines) - 1):
                    if idx not in session.sealed:
                        final_queue.put_nowait((ident, idx, lines[idx]))
                        session.sealed.add(idx)

                active_idx = len(lines) - 1
                _queue_latest(partial_queues[ident], (active_idx, lines[active_idx]))

        async def route_window(
            window: "np.ndarray",
            increment_start: int,
            increment_len: int,
            global_start_sample: int,
        ) -> None:
            """Sépare `window` (les dernières SEPARATION_WINDOW_S secondes d'audio brut
            accumulé) et route seulement le nouvel incrément (`[increment_start:
            increment_start + increment_len]`, la partie jamais encore envoyée à un
            processor — cf. "le passé est immuable", Loom/CLAUDE.md) vers l'identité
            correspondante. `global_start_sample` : position de cet incrément dans le flux
            source global (pas relative à `window`) — enregistrée via `_record_send` pour
            pouvoir retraduire les timestamps internes WLK plus tard (cf. `identity_timeline`).

            ⚠ Appelée depuis `route_consumer` (tâche de fond), jamais depuis `send` — cf.
            Révisions ADR-0044 : appeler ce genre de traitement GPU directement dans `send`
            (attendu par `replay_realtime` avant le chunk suivant) désynchronise le pacing
            temps réel dès que ce traitement dépasse le débit d'arrivée de l'audio, ce que le
            premier run réel a confirmé (étage `wlk` p95=9s, largement hors budget).

            Quand un seul locuteur est actif (`streams_are_distinct` faux), les identités
            inactives reçoivent du silence (zéros) plutôt qu'aucun chunk du tout — constaté
            empiriquement (cf. Révisions ADR-0044) : ne rien envoyer pendant de longues
            secondes à une session WLK fait grimper sa latence de ~4x par rapport à une
            identité alimentée en continu, probablement parce que WLK suppose un flux
            continu. Le silence garde l'horloge interne de la session alignée sur l'horloge
            murale sans lui faire "entendre" du contenu qu'elle n'a pas.

            Sous `MIN_SEPARATION_AUDIO_S` de contexte accumulé (tout début de flux, avant que
            `window` atteigne une taille utile), `separator.separate` n'est même pas appelé —
            proposition de Kevin (2026-07-17) : pas assez de matière pour que SepFormer sépare
            utilement, autant économiser l'appel et router l'audio brut directement (même
            chemin que "un seul locuteur actif" ci-dessus, cf. `pick_active_identity`).
            """
            too_short_for_separation = len(window) / SAMPLE_RATE_HZ < MIN_SEPARATION_AUDIO_S

            t0 = time.monotonic()
            streams = None if too_short_for_separation else await asyncio.to_thread(
                separator.separate, window
            )
            if streams is not None and len(streams) > N_IDENTITIES:
                # pyannote/separation-ami-1.0 peut retourner jusqu'à 3 flux — on ne gère que
                # N_IDENTITIES=2 aujourd'hui (cf. ADR-0044, pas encore généralisé à N), les
                # flux excédentaires sont ignorés plutôt que de planter assign_and_bootstrap.
                streams = streams[:N_IDENTITIES]
            t_separate_s = time.monotonic() - t0

            t0 = time.monotonic()
            stream_embeddings = None
            if not too_short_for_separation:
                stream_embeddings = list(
                    await asyncio.gather(*(asyncio.to_thread(embedder.embed, s) for s in streams))
                )
            t_embed_s = time.monotonic() - t0

            t0 = time.monotonic()
            if not too_short_for_separation and streams_are_distinct(stream_embeddings):
                distinct_count[0] += 1
                assignment = assign_and_bootstrap(known_embeddings, stream_embeddings)
                sends = []
                assignment_debug = []
                for ident in range(N_IDENTITIES):
                    stream_idx = assignment[ident]
                    prior = known_embeddings[ident]
                    similarity = (
                        cosine_similarity(prior, stream_embeddings[stream_idx])
                        if prior is not None
                        else float("nan")
                    )
                    # `assign_and_bootstrap` choisit toujours la MEILLEURE paire disponible,
                    # même mauvaise dans l'absolu — sans ce garde-fou, une identité peut
                    # dériver silencieusement vers le mauvais locuteur (constaté en pratique,
                    # 2026-07-17 : une identité a mélangé le contenu des deux locuteurs
                    # originaux d'un run, similarité 0,45, sous le seuil). Correspondance
                    # incertaine → incrément ignoré (ni routage, ni mise à jour de
                    # l'embedding roulant) plutôt que de corrompre le suivi d'identité.
                    if not is_confident_match(prior, stream_embeddings[stream_idx]):
                        assignment_debug.append(
                            f"id{ident}<-stream{stream_idx}(sim={similarity:.2f}, REJETÉ)"
                        )
                        continue
                    assignment_debug.append(f"id{ident}<-stream{stream_idx}(sim={similarity:.2f})")
                    known_embeddings[ident] = update_running_embedding(
                        known_embeddings[ident],
                        stream_embeddings[stream_idx],
                        embedding_counts[ident],
                    )
                    embedding_counts[ident] += 1
                    increment_audio = streams[stream_idx][
                        increment_start : increment_start + increment_len
                    ]
                    _record_send(ident, len(increment_audio), global_start_sample)
                    sends.append(
                        sessions[ident].processor.process_audio(_pcm16_bytes(increment_audio))
                    )
                await asyncio.gather(*sends)
                print(f"DEBUG assignment: {' '.join(assignment_debug)}")
            else:
                if too_short_for_separation:
                    too_short_count[0] += 1
                else:
                    non_distinct_count[0] += 1
                mixture_embedding = await asyncio.to_thread(embedder.embed, window)
                active_ident = pick_active_identity(known_embeddings, mixture_embedding)
                known_embeddings[active_ident] = update_running_embedding(
                    known_embeddings[active_ident],
                    mixture_embedding,
                    embedding_counts[active_ident],
                )
                embedding_counts[active_ident] += 1
                increment_audio = window[increment_start : increment_start + increment_len]
                _record_send(active_ident, len(increment_audio), global_start_sample)
                # ⚠ Retour à "rien envoyé" aux identités inactives (2026-07-17) : le silence-fill
                # (cf. Révisions ADR-0044) n'a montré aucun effet mesuré sur la latence lors de
                # son propre test, et une dégradation sévère de qualité est apparue après son
                # ajout (une identité quasi entièrement "[inaudible]" alors qu'elle recevait
                # pourtant du contenu réel) — retiré le temps de confirmer/infirmer via
                # l'instrumentation de stabilité d'assignation ci-dessus plutôt que de garder
                # deux changements non isolés en même temps.
                await sessions[active_ident].processor.process_audio(
                    _pcm16_bytes(increment_audio)
                )
            t_route_s = time.monotonic() - t0

            print(
                f"DEBUG route_window: window={len(window) / SAMPLE_RATE_HZ:.1f}s "
                f"separate={t_separate_s * 1000:.0f}ms embed={t_embed_s * 1000:.0f}ms "
                f"route={t_route_s * 1000:.0f}ms total={(t_separate_s + t_embed_s + t_route_s) * 1000:.0f}ms "
                f"distinct={distinct_count[0]} non_distinct={non_distinct_count[0]} "
                f"too_short={too_short_count[0]} "
                f"content_s={[round(c, 1) for c in content_seconds]} "
                f"silence_s={[round(s, 1) for s in silence_seconds]}"
            )

        async def route_consumer(route_queue: "asyncio.Queue") -> None:
            """Draine `route_queue` en séquence (préserve l'ordre, évite les races sur
            `known_embeddings`/`embedding_counts`), découplé du rythme temps réel de `feed`
            (cf. docstring de `route_window`)."""
            while True:
                print(f"DEBUG route_consumer: file en attente = {route_queue.qsize()}")
                window, increment_start, increment_len, global_start_sample = (
                    await route_queue.get()
                )
                try:
                    await route_window(window, increment_start, increment_len, global_start_sample)
                except asyncio.CancelledError:
                    raise
                except Exception as exc:  # noqa: BLE001 — isole une erreur de séparation/routage
                    print(f"WARNING: route_window a échoué ({exc!r}) — incrément perdu.")
                finally:
                    route_queue.task_done()

        async def feed(route_queue: "asyncio.Queue") -> None:
            import numpy as np

            buffer = np.zeros(0, dtype=np.float32)
            routed_samples = 0
            window_samples = int(separation_window_s * SAMPLE_RATE_HZ)
            route_every_samples = int(ROUTE_EVERY_S * SAMPLE_RATE_HZ)
            drop_count = [0]  # cf. DEBUG print après route_queue.join()

            async def send(chunk_bytes: bytes) -> None:
                nonlocal buffer, routed_samples

                pcm16 = np.frombuffer(chunk_bytes, dtype=np.int16)
                buffer = np.concatenate([buffer, pcm16.astype(np.float32) / 32768.0])

                unrouted = len(buffer) - routed_samples
                if unrouted < route_every_samples:
                    return

                window_start = max(0, len(buffer) - window_samples)
                window = buffer[window_start:].copy()
                increment_start = routed_samples - window_start
                increment_len = len(buffer) - routed_samples
                global_start_sample = routed_samples
                # Marqué routé immédiatement (avant même que route_consumer n'ait traité le
                # job) : `send` ne doit jamais attendre le traitement GPU, cf. docstring de
                # route_window. Politique de drop si route_consumer est en retard : le plus
                # ancien job en attente saute (audio réellement perdu pour ce tour de parole,
                # pas rejoué plus tard — "le passé est immuable"), jamais `send` ne bloque
                # (cf. "jamais de blocage amont", Loom/CLAUDE.md).
                routed_samples = len(buffer)
                job = (window, increment_start, increment_len, global_start_sample)
                try:
                    route_queue.put_nowait(job)
                except asyncio.QueueFull:
                    drop_count[0] += 1
                    try:
                        route_queue.get_nowait()
                        route_queue.task_done()
                    except asyncio.QueueEmpty:
                        pass
                    route_queue.put_nowait(job)

            await replay_realtime(wav_path, send)

            # Reliquat plus court que ROUTE_EVERY_S en fin de flux — routé sans throttle,
            # même politique de drop que send() si la file est pleine.
            unrouted = len(buffer) - routed_samples
            if unrouted > 0:
                window_start = max(0, len(buffer) - window_samples)
                window = buffer[window_start:].copy()
                increment_start = routed_samples - window_start
                job = (window, increment_start, unrouted, routed_samples)
                try:
                    route_queue.put_nowait(job)
                except asyncio.QueueFull:
                    drop_count[0] += 1
                    try:
                        route_queue.get_nowait()
                        route_queue.task_done()
                    except asyncio.QueueEmpty:
                        pass
                    route_queue.put_nowait(job)

            await route_queue.join()
            print(f"DEBUG feed: jobs droppés (file pleine) = {drop_count[0]}")
            for session in sessions:
                await session.processor.process_audio(b"")

        # File bornée (politique de drop du plus ancien, cf. `send`) entre le rythme temps
        # réel de `feed` et le traitement GPU de `route_consumer` — ROUTE_QUEUE_MAXSIZE jobs
        # de ROUTE_EVERY_S chacun, donc quelques secondes de retard tolérées avant de perdre
        # de l'audio. ⚠ Taille pas calibrée empiriquement, valeur de départ.
        route_queue: asyncio.Queue = asyncio.Queue(maxsize=ROUTE_QUEUE_MAXSIZE)
        route_consumer_task = asyncio.create_task(route_consumer(route_queue))
        commit_worker_task = asyncio.create_task(commit_worker())
        consumer_tasks = [asyncio.create_task(consume(ident)) for ident in range(N_IDENTITIES)]
        await feed(route_queue)
        await asyncio.sleep(2.0)

        # Laisse commit_worker vider ce qui reste avant de tout couper — un backlog éventuel
        # (cf. Révisions ADR-0044) doit finir de se traiter, pas être tronqué silencieusement.
        await final_queue.join()
        for queue in partial_queues:
            await queue.join()

        route_consumer_task.cancel()
        commit_worker_task.cancel()
        for task in consumer_tasks:
            task.cancel()

        for ident, session in enumerate(sessions):
            for idx, line in enumerate(session.last_lines):
                if idx not in session.sealed:
                    await force_final_commit_llm(ident, idx, line)

    return PipelineDualBenchmarkResult(
        log_path=log_path, transcript_path=transcript_path, audio_dir=audio_dir
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Câblage bout-en-bout avec séparation en amont de WLK (ADR-0044) : "
        "audio brut séparé en continu, routé vers un AudioProcessor par identité suivie, "
        "traduction LlmTranslator (ADR-0043) + Pocket TTS. Premier jet, pas encore validé."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--lan", default="auto")
    parser.add_argument("--target-lang", default="fr")
    parser.add_argument(
        "--separator-backend",
        choices=["sepformer", "pyannote"],
        default="sepformer",
        help="Modèle de séparation de voix (ADR-0044) — 'pyannote' (pyannote/separation-ami-1.0) "
        "nécessite HF_TOKEN (modèle à accès conditionnel).",
    )
    args = parser.parse_args()

    result = asyncio.run(
        run_benchmark(
            args.corpus_key,
            args.out_dir,
            args.corpus_dir,
            lan=args.lan,
            target_lang=args.target_lang,
            separator_backend=args.separator_backend,
        )
    )

    events = load_events(result.log_path)
    reports = aggregate_by_stage(events)
    end_to_end = aggregate_end_to_end(events)
    if end_to_end is not None:
        reports.append(end_to_end)

    print(format_report(reports))
    print(f"\nLog : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")
    print(f"Audio FR par identité/ligne : {result.audio_dir}")


if __name__ == "__main__":
    main()
