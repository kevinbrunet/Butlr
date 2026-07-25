from __future__ import annotations

import argparse
import asyncio
import time
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# ✓ Vérifié par lecture directe de whisperlivekit/__init__.py (repo QuentinFuxa/WhisperLiveKit,
# commit lu le 2026-07-07) : `from whisperlivekit import AudioProcessor, TranscriptionEngine`
# est l'import public exact.
from whisperlivekit import AudioProcessor, TranscriptionEngine

from loom_orchestrator.alignatt import compute_increment
from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.aggregate import (
    aggregate_by_stage,
    aggregate_end_to_end,
    format_report,
    load_events,
)
from loom_orchestrator.bench.audio_chunks import read_segment
from loom_orchestrator.bench.instrumentation import (
    STAGE_SEAMLESS,
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
from loom_orchestrator.commit_state import (
    MIN_NEW_AUDIO_S,
    LineCommitState,
    _consume_stream,
    _release_gpu_state,
)
from loom_orchestrator.speaker_separation import SAMPLE_RATE_HZ, SpeakerEmbedder, VoiceSeparator
from loom_orchestrator.speaker_tracking import (
    MATCH_CONFIDENCE_THRESHOLD,
    pick_matching_stream,
    streams_are_distinct,
    update_running_embedding,
)
from loom_orchestrator.translation_llm import LlmTranslator
from loom_orchestrator.translation_seamless import AlignAttSeamlessTranslator, SeamlessTranslator
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ⚠ Pas calibrées empiriquement (ADR-0042). SEPARATION_WINDOW_S ~ zone de confort mesurée de
# SepFormer-WHAMR (moyenne d'entraînement WHAMR ~5,6s, saturation des performances ~5,8s) —
# jamais toute la ligne d'un coup (coût quadratique de l'attention sur de l'audio long, cf.
# le problème déjà rencontré avec AlignAtt sur un monologue continu, corpus `a`).
# MIN_SEPARATION_AUDIO_S : en dessous, pas assez de contexte pour que la passe inter-segments
# de SepFormer serve à quelque chose (cf. discussion en chat sur le découpage dual-path).
SEPARATION_WINDOW_S = 6.0
MIN_SEPARATION_AUDIO_S = 2.0


@dataclass
class PipelineBenchmarkResult:
    log_path: Path
    transcript_path: Path
    audio_dir: Path
    final_fr_by_line: dict = field(default_factory=dict)


def _write_wav(path: Path, audio: "np.ndarray", sample_rate_hz: int) -> None:
    # ⚠ Format de sortie de TTSModel.generate_audio_stream() non confirmé par exécution
    # réelle (cf. tts_pocket.PocketTtsSynthesizer) — on suppose du float dans [-1, 1] comme
    # la plupart des TTS, et on clippe avant conversion PCM16. À corriger si le premier run
    # réel montre un tenseur déjà en int16 (le clip/scale serait alors silencieusement faux).
    import numpy as np

    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


def _separate_and_track(
    separator: VoiceSeparator,
    embedder: SpeakerEmbedder,
    window: "np.ndarray",
    known_embedding: list | None,
) -> tuple["np.ndarray | None", list]:
    """Sépare `window` en 2 flux et choisit celui qui correspond le mieux à
    `known_embedding` (ADR-0042) — ou, à défaut d'embedding déjà sauvegardé, au mélange brut
    lui-même (WLK a déjà attribué ce segment à ce locuteur, donc le flux séparé le plus
    proche du mélange global est le candidat le plus probable pour être ce locuteur).

    Retourne `(flux_choisi_ou_None, embedding_à_sauvegarder)`. `None` si les deux flux
    séparés ne semblent pas correspondre à des voix distinctes (rien de réel à séparer, cas
    courant d'un seul locuteur actif) ou si la correspondance est trop incertaine — dans ces
    deux cas l'appelant doit garder l'audio brut inchangé.
    """
    streams = separator.separate(window)
    stream_embeddings = [embedder.embed(s) for s in streams]

    if not streams_are_distinct(stream_embeddings):
        return None, embedder.embed(window)

    reference = known_embedding if known_embedding is not None else embedder.embed(window)
    matched_idx, similarity = pick_matching_stream(reference, stream_embeddings)
    if similarity < MATCH_CONFIDENCE_THRESHOLD:
        return None, reference
    return streams[matched_idx], stream_embeddings[matched_idx]


async def run_benchmark(
    corpus_key: str,
    out_dir: Path,
    corpus_dir: Path = corpus.CORPUS_DIR,
    diarization: bool = True,
    lan: str = "auto",
    target_lang: str = "fr",
    separation: bool = True,
    translator: str = "seamless",
) -> PipelineBenchmarkResult:
    """Câblage bout-en-bout (préliminaire à T2.3) : WLK (STT+diarisation) → traduction →
    Pocket TTS (synthèse FR, voix de repli unique, pas de clonage par locuteur — cf.
    `tts_pocket.py`). Pas encore l'orchestrateur final : pas de file bornée, pas de registre
    de voix par locuteur (cf. `main.py`, toujours `NotImplementedError` pour le vrai T2.3).

    `translator` sélectionne l'étage de traduction (ADR-0043, les deux chemins cohabitent
    tant que le remplacement n'est pas validé, cf. convention "mesurer avant d'optimiser") :
    - `"seamless"` (défaut, inchangé) : `AlignAttSeamlessTranslator`/`SeamlessTranslator`,
      traduction audio-native, commit par attention croisée (ADR-0041). Voir docstring
      ci-dessous pour le détail.
    - `"llm"` : `LlmTranslator` (ADR-0043), traduction du **texte** WLK déjà transcrit (pas
      l'audio) — commit par segmentation ponctuation/pause (`commit_policy.compute_flush`)
      sur le texte source, pas par attention croisée. ⚠ Conséquence pas explicitée dans
      ADR-0043 avant ce câblage : la séparation de voix (ADR-0042) nettoyait l'audio juste
      avant l'appel Seamless — elle n'a plus de consommateur avec ce chemin, qui ne touche
      jamais à l'audio pour la traduction. `separator`/`embedder` ne sont donc pas
      instanciés quand `translator="llm"`, quelle que soit la valeur de `separation`.

    Politique de commit (ADR-0041, révisée le 2026-07-15 — remplace une première version
    "attend la fin du flux", elle-même remplaçant une version encore antérieure "scelle sur
    nouvel index WLK", jamais implémentée) : pendant qu'une ligne WLK grandit, l'audio
    disponible pour cette ligne est retraduit par `AlignAttSeamlessTranslator.
    translate_partial` (throttlé par `MIN_NEW_AUDIO_S`), qui ne retourne que le préfixe
    "sûr" au sens AlignAtt. Le nouveau préfixe est diffé contre le texte déjà commité
    (`alignatt.compute_increment`) — seul l'increment part vers Pocket TTS. Quand une ligne
    se termine (nouvel index WLK = changement de locuteur, ou fin du flux), le texte restant
    est commité de force via `SeamlessTranslator.translate` (traduction complète, l'audio ne
    grandira plus) — filet de sécurité, cf. "le passé est immuable" (`Loom/CLAUDE.md`).

    ⚠ Le traitement d'un increment (Seamless + TTS, déportés en executor via
    `asyncio.to_thread`) est awaited séquentiellement dans la tâche qui consomme aussi les
    résultats WLK : un increment lent ralentit la lecture du flux de résultats WLK (jamais
    son traitement interne, cf. "TTS en retard = dégradation contrôlée, jamais de blocage
    amont"), et fait grandir la file interne de résultats déjà bufferisée par WLK. Pas de
    plafond explicite ici — c'est le travail de l'orchestrateur final (T2.3).

    Le chunking côté Seamless (dicté par AlignAtt, cf. ci-dessus) et le chunking audio côté
    TTS sont découplés (2026-07-15, suite à une question de Kevin) : chaque increment
    déclenche son propre appel TTS, tous accumulés dans `line{idx}.wav` (un seul fichier par
    ligne, pas un par increment, réécrit à chaque nouvel increment). ⚠ Chaque appel repart de
    l'état vocal de base (`synthesize_stream`, `copy_state=True`) depuis le 2026-07-25 (cf.
    Révisions ADR-0041) — l'enchaînement continu par `copy_state=False`
    (`new_line_state`/`synthesize_continuation`) essayé initialement fait dégénérer Pocket
    TTS en boucle audio (constaté par exécution réelle, reproduit 5/5 en isolation). Rupture
    de prosodie/débit à chaque frontière d'increment assumée pour l'instant.

    Séparation de voix + suivi d'identité par embedding (ADR-0042, `separation=True` par
    défaut, désactivable via `--no-separation`) : avant chaque traduction (partielle ou
    finale), les `SEPARATION_WINDOW_S` dernières secondes de l'audio de la ligne sont
    passées à `VoiceSeparator` (SepFormer-WHAMR) ; le flux de sortie le plus proche de
    l'embedding déjà connu pour cette ligne (ou, à défaut, du mélange brut lui-même)
    remplace la fenêtre brute avant traduction — approxime l'extraction ciblée avec des
    briques matures (séparation aveugle + suivi par embedding), faute de modèle d'extraction
    ciblée disponible prêt à l'emploi (cf. ADR-0042). N'agit jamais que sur la fenêtre finale
    (jamais la ligne entière — coût quadratique de l'attention sur de l'audio long) ; le
    préfixe plus ancien de la ligne reste brut.
    """
    corpus.validate(corpus_key, corpus_dir=corpus_dir)
    wav_path = corpus.resolve(corpus_key, corpus_dir=corpus_dir)

    run_id = f"{corpus_key}-pipeline-{int(time.time())}"
    log_path = out_dir / f"{run_id}.jsonl"
    transcript_path = out_dir / f"{run_id}.transcript.txt"
    audio_dir = out_dir / f"{run_id}-audio"
    out_dir.mkdir(parents=True, exist_ok=True)
    audio_dir.mkdir(parents=True, exist_ok=True)
    transcript_path.write_text("", encoding="utf-8")

    engine = TranscriptionEngine(
        pcm_input=True,
        diarization=diarization,
        diarization_backend="sortformer",
        lan=lan,
    )
    processor = AudioProcessor(transcription_engine=engine, mode="full")
    synth = PocketTtsSynthesizer()

    alignatt_translator = AlignAttSeamlessTranslator() if translator == "seamless" else None
    final_translator = SeamlessTranslator() if translator == "seamless" else None
    llm_translator = LlmTranslator() if translator == "llm" else None
    # Cf. docstring ci-dessus : la séparation de voix (ADR-0042) n'a de rôle que pour nettoyer
    # l'audio avant un appel Seamless — orpheline avec le traducteur LLM (texte, pas audio).
    separator = VoiceSeparator() if (separation and translator == "seamless") else None
    embedder = SpeakerEmbedder() if (separation and translator == "seamless") else None

    # ⚠ Un seul `source_lang` par run (ADR-0043) : le petit Qwen a besoin d'un code langue
    # explicite par appel (`translation_llm.LANGUAGE_NAMES`), contrairement à Seamless qui
    # détecte/accepte `lan="auto"` en amont côté WLK. On suppose ici un seul locuteur/langue
    # source par fichier corpus — vrai pour toutes les entrées de `corpus.CORPUS_MANIFEST` à
    # ce jour (pas de code-switching intra-fichier), pas garanti en usage réel multi-locuteur.
    source_lang = next(c for c in corpus.CORPUS_MANIFEST if c.key == corpus_key).language

    # ⚠ Instrumentation temporaire (2026-07-15, cf. Révisions ADR-0042) : isole la VRAM déjà
    # occupée par les modèles résidents (WLK/Sortformer/Seamless×2/Pocket TTS/SepFormer/ECAPA
    # si séparation active) avant tout traitement — sert à distinguer une pression de base
    # (trop de modèles chargés simultanément) d'une croissance liée à translate_partial.
    import torch

    if torch.cuda.is_available():
        baseline_allocated_gb = torch.cuda.memory_allocated() / 1e9
        baseline_reserved_gb = torch.cuda.memory_reserved() / 1e9
        print(
            f"DEBUG VRAM baseline (tous modèles chargés, avant traitement) : "
            f"allocated={baseline_allocated_gb:.2f}GB reserved={baseline_reserved_gb:.2f}GB"
        )

    with EventLogger(log_path) as logger:
        replay_start_monotonic = time.monotonic()
        known_texts: list[str] = []
        last_lines: list[dict] = []
        commit_state: dict[int, LineCommitState] = {}
        sealed_committed: set[int] = set()

        async def send(chunk_bytes: bytes) -> None:
            await processor.process_audio(chunk_bytes)

        async def clean_audio_for_line(idx: int, source_audio: "np.ndarray") -> "np.ndarray":
            import numpy as np

            if separator is None or embedder is None:
                return source_audio

            window_samples = int(SEPARATION_WINDOW_S * SAMPLE_RATE_HZ)
            min_samples = int(MIN_SEPARATION_AUDIO_S * SAMPLE_RATE_HZ)
            if len(source_audio) < min_samples:
                return source_audio

            if len(source_audio) > window_samples:
                prefix = source_audio[:-window_samples]
                window = source_audio[-window_samples:]
            else:
                prefix = np.array([], dtype=source_audio.dtype)
                window = source_audio

            state = commit_state.setdefault(idx, LineCommitState())
            cleaned_window, embedding = await asyncio.to_thread(
                _separate_and_track, separator, embedder, window, state.embedding
            )
            state.embedding = update_running_embedding(
                state.embedding, embedding, state.embedding_count
            )
            state.embedding_count += 1

            if cleaned_window is None:
                return source_audio
            return np.concatenate([prefix, cleaned_window])

        async def emit_increment(
            idx: int,
            speaker: str,
            increment: str,
            event_stage_t_in: float,
            is_final: bool = False,
        ) -> None:
            if not increment:
                return
            state = commit_state[idx]

            new_chunks, ttfc_s = await asyncio.to_thread(_consume_stream, synth, increment)
            t_first_chunk = event_stage_t_in + ttfc_s
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_TTS, event_stage_t_in, t_first_chunk, is_final=is_final
                )
            )

            state.audio_chunks.extend(new_chunks)
            import numpy as np

            full_audio = np.concatenate(state.audio_chunks)
            _write_wav(audio_dir / f"line{idx}.wav", full_audio, synth.sample_rate_hz)

            with transcript_path.open("a", encoding="utf-8") as f:
                f.write(f"[{speaker}] FR (increment) : {increment}\n")
            state.chunk_count += 1

        async def try_alignatt_commit(idx: int, line: dict) -> None:
            start, end = line.get("start"), line.get("end")
            if start is None or end is None:
                return
            start_s, end_s = hms_to_seconds(start), hms_to_seconds(end)

            state = commit_state.setdefault(idx, LineCommitState())
            if end_s - state.last_alignatt_end_s < MIN_NEW_AUDIO_S:
                return
            state.last_alignatt_end_s = end_s

            source_audio = read_segment(wav_path, start_s, end_s)
            audio_len_s = len(source_audio) / SAMPLE_RATE_HZ

            t0 = time.monotonic()
            source_audio = await clean_audio_for_line(idx, source_audio)
            t_clean_s = time.monotonic() - t0

            import torch

            if torch.cuda.is_available():
                torch.cuda.reset_peak_memory_stats()

            t0 = time.monotonic()
            safe_text = await asyncio.to_thread(
                alignatt_translator.translate_partial, source_audio, target_lang
            )
            t_translate_s = time.monotonic() - t0

            # ⚠ Instrumentation temporaire (2026-07-15, cf. Révisions ADR-0042) : durée brute
            # par appel (pas le "retard cumulé" de STAGE_SEAMLESS) + pic mémoire alloué durant
            # ce seul appel — sert à confirmer si le pic mémoire de translate_partial grandit
            # avec l'audio (cause probable des OOM constatés en plus de la dérive de latence).
            peak_gb = torch.cuda.max_memory_allocated() / 1e9 if torch.cuda.is_available() else 0.0
            print(
                f"DEBUG line{idx} chunk{state.chunk_count}: audio={audio_len_s:.1f}s "
                f"clean={t_clean_s * 1000:.0f}ms translate={t_translate_s * 1000:.0f}ms "
                f"peak_translate={peak_gb:.2f}GB"
            )

            t_translate_end = time.monotonic() - replay_start_monotonic
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_SEAMLESS, end_s, t_translate_end))

            increment, is_consistent = compute_increment(state.committed_fr, safe_text)
            if not is_consistent:
                print(
                    f"WARNING: AlignAtt incohérent sur line{idx} — texte sûr précédent non "
                    f"préfixé par le nouveau (committed={state.committed_fr!r}, "
                    f"new_safe={safe_text!r}). Increment ignoré, texte déjà commité conservé."
                )
                return

            state.committed_fr = safe_text
            speaker = line.get("speaker", "?")
            await emit_increment(idx, speaker, increment, t_translate_end)

        async def force_final_commit(idx: int, line: dict) -> None:
            text = line.get("text")
            start, end = line.get("start"), line.get("end")
            if not text or start is None or end is None:
                return
            start_s, end_s = hms_to_seconds(start), hms_to_seconds(end)
            speaker = line.get("speaker", "?")

            source_audio = read_segment(wav_path, start_s, end_s)
            source_audio = await clean_audio_for_line(idx, source_audio)
            final_fr = await asyncio.to_thread(
                final_translator.translate, source_audio, target_lang
            )
            t_translate_end = time.monotonic() - replay_start_monotonic
            state = commit_state.setdefault(idx, LineCommitState())
            # Même format que emit_increment (chunk{state.chunk_count}, pas "-final") — sinon
            # ce commit final ne se chaîne jamais avec son propre événement TTS dans
            # aggregate_end_to_end (2026-07-19, cf. ADR-0044 §Révisions, bug trouvé par Kevin).
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(
                LatencyEvent.create(
                    segment_id, STAGE_SEAMLESS, end_s, t_translate_end, is_final=True
                )
            )

            increment, is_consistent = compute_increment(state.committed_fr, final_fr)
            if is_consistent:
                state.committed_fr = final_fr
                await emit_increment(idx, speaker, increment, t_translate_end, is_final=True)
            else:
                print(
                    f"WARNING: traduction finale de line{idx} incohérente avec le texte déjà "
                    f"commité (committed={state.committed_fr!r}, final={final_fr!r}). "
                    "Texte déjà commité conservé, tail final ignoré."
                )

            # Ligne définitivement scellée à ce point (dernier appel jamais fait pour idx) —
            # libère l'état GPU retenu, cf. fuite mémoire constatée (Révisions ADR-0042).
            _release_gpu_state(state)

        async def try_llm_commit(idx: int, line: dict) -> None:
            """Équivalent de `try_alignatt_commit` pour `translator="llm"` (ADR-0043) : pas
            d'audio, pas d'attention croisée — segmente le **texte** WLK déjà transcrit sur
            ponctuation/pause (`commit_policy.compute_flush`) et traduit chaque nouveau
            segment complet (pas de préfixe "sûr" à rediffer, `compute_flush` ne retourne
            déjà que la partie neuve)."""
            text, end = line.get("text"), line.get("end")
            if not text or end is None:
                return

            state = commit_state.setdefault(idx, LineCommitState())
            segment, new_flushed, is_consistent = compute_flush(text, state.flushed_source)
            if not is_consistent:
                print(
                    f"WARNING: WLK a révisé du texte déjà flushé sur line{idx} (source="
                    f"{text!r}, déjà flushé={state.flushed_source!r}). Increment ignoré, "
                    "texte déjà flushé conservé."
                )
                return
            state.flushed_source = new_flushed
            if not segment:
                return

            end_s = hms_to_seconds(end)
            translated = await asyncio.to_thread(
                llm_translator.translate, segment, source_lang, target_lang
            )
            t_translate_end = time.monotonic() - replay_start_monotonic
            segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
            logger.log(LatencyEvent.create(segment_id, STAGE_TRANSLATE_LLM, end_s, t_translate_end))

            state.committed_fr = f"{state.committed_fr} {translated}".strip()
            speaker = line.get("speaker", "?")
            await emit_increment(idx, speaker, translated, t_translate_end)

        async def force_final_commit_llm(idx: int, line: dict) -> None:
            """Équivalent de `force_final_commit` pour `translator="llm"` : flush le texte
            source restant sans attendre de ponctuation (`commit_policy.force_flush`,
            l'audio de cette ligne ne grandira plus) puis traduit."""
            text, end = line.get("text"), line.get("end")
            state = commit_state.setdefault(idx, LineCommitState())
            # `end` sert seulement à ancrer le log de latence — ne jamais en faire une
            # condition pour flush le texte lui-même : c'est le dernier appel jamais fait
            # pour cet idx, un `end` manquant (WLK n'a pas calculé de timestamp final sur
            # une coupure en plein milieu de phrase) ne doit pas faire disparaître du
            # contenu déjà transcrit sans aucun WARNING (bug trouvé par Kevin, cf. ADR-0044
            # §Révisions).
            if text:
                segment, new_flushed, is_consistent = force_flush(text, state.flushed_source)
                if not is_consistent:
                    print(
                        f"WARNING: traduction finale (llm) de line{idx} incohérente avec le "
                        f"texte déjà flushé (déjà flushé={state.flushed_source!r}, source="
                        f"{text!r}). Tail final ignoré."
                    )
                else:
                    state.flushed_source = new_flushed
                    if segment:
                        translated = await asyncio.to_thread(
                            llm_translator.translate, segment, source_lang, target_lang
                        )
                        t_translate_end = time.monotonic() - replay_start_monotonic
                        # Même format que emit_increment (chunk{state.chunk_count}, pas
                        # "-final") — cf. force_final_commit ci-dessus, même correctif.
                        segment_id = f"{corpus_key}-line{idx}-chunk{state.chunk_count}"
                        if end is not None:
                            end_s = hms_to_seconds(end)
                            logger.log(
                                LatencyEvent.create(
                                    segment_id,
                                    STAGE_TRANSLATE_LLM,
                                    end_s,
                                    t_translate_end,
                                    is_final=True,
                                )
                            )
                        state.committed_fr = f"{state.committed_fr} {translated}".strip()
                        speaker = line.get("speaker", "?")
                        await emit_increment(idx, speaker, translated, t_translate_end, is_final=True)

            # Ligne définitivement scellée à ce point — même nettoyage GPU que le chemin
            # Seamless (cf. `force_final_commit`), même si `translator="llm"` ne charge pas
            # les mêmes modèles : `audio_chunks` (accumulé pour `line{idx}.wav`) reste commun
            # aux deux chemins.
            _release_gpu_state(state)

        async def consume() -> None:
            results_generator = await processor.create_tasks()
            async for response in results_generator:
                data = response.to_dict()
                lines = data.get("lines", [])
                last_lines[:] = lines

                for idx, line, _text in extract_updates(lines, known_texts):
                    end = line.get("end")
                    if end is None:
                        continue
                    segment_id = f"{corpus_key}-wlk-line{idx}-{len(_text)}"
                    t_in = hms_to_seconds(end)
                    t_out = time.monotonic() - replay_start_monotonic
                    logger.log(LatencyEvent.create(segment_id, STAGE_WLK, t_in, t_out))

                if not lines:
                    continue

                # Scelle définitivement toute ligne qui n'est plus la dernière (changement
                # de locuteur détecté) et pas encore scellée — traduction complète, pas
                # partielle, l'audio de cette ligne ne grandira plus.
                for idx in range(len(lines) - 1):
                    if idx not in sealed_committed:
                        if translator == "llm":
                            await force_final_commit_llm(idx, lines[idx])
                        else:
                            await force_final_commit(idx, lines[idx])
                        sealed_committed.add(idx)

                # Commit incrémental sur la ligne active (la dernière, encore en croissance).
                # Chemin seamless : throttlé par MIN_NEW_AUDIO_S côté audio. Chemin llm :
                # naturellement throttlé par `compute_flush` (rien à faire tant qu'aucun
                # nouveau point de segmentation n'est apparu dans le texte).
                active_idx = len(lines) - 1
                if translator == "llm":
                    await try_llm_commit(active_idx, lines[active_idx])
                else:
                    await try_alignatt_commit(active_idx, lines[active_idx])

        consumer_task = asyncio.create_task(consume())
        await replay_realtime(wav_path, send)
        await processor.process_audio(b"")  # signale la fin du flux (cf. API WLK)
        await asyncio.sleep(2.0)
        consumer_task.cancel()

        # `sealed_committed` ne doit jamais conditionner ce dernier appel : `lines` n'est
        # pas append-only (cf. Loom/CLAUDE.md, ADR-0039) — un idx peut être scellé par
        # erreur si WLK a transitoirement eu plus de lignes avant de fusionner/rewinder, ce
        # qui bloquait alors ce idx pour toujours (bug trouvé par Kevin sur `main.py`, même
        # code ici — tail final jamais commis malgré du contenu réel restant).
        # `force_final_commit`/`force_final_commit_llm` sont idempotents (segment/increment
        # vide si rien de neuf) — les appeler sans condition est donc toujours sûr.
        for idx, line in enumerate(last_lines):
            if translator == "llm":
                await force_final_commit_llm(idx, line)
            else:
                await force_final_commit(idx, line)

    return PipelineBenchmarkResult(
        log_path=log_path,
        transcript_path=transcript_path,
        audio_dir=audio_dir,
        final_fr_by_line={idx: s.committed_fr for idx, s in commit_state.items()},
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Câblage bout-en-bout Loom : WLK (STT+diarisation) → commit AlignAtt "
        "(ADR-0041) → SeamlessM4T v2 → Pocket TTS (synthèse FR), incrémental par ligne."
    )
    parser.add_argument("corpus_key", choices=[c.key for c in corpus.CORPUS_MANIFEST])
    parser.add_argument("--out-dir", type=Path, default=Path("bench-runs"))
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument("--no-diarization", action="store_true")
    parser.add_argument(
        "--no-separation",
        action="store_true",
        help="Désactive la séparation de voix + suivi par embedding (ADR-0042) — pour "
        "comparer avec/sans (cf. convention 'mesurer avant d'optimiser').",
    )
    parser.add_argument("--lan", default="auto")
    parser.add_argument("--target-lang", default="fr")
    parser.add_argument(
        "--translator",
        choices=["seamless", "llm"],
        default="seamless",
        help="Étage de traduction (ADR-0043) : 'seamless' (défaut, inchangé, audio-native) "
        "ou 'llm' (petit Qwen local sur le texte WLK — désactive la séparation de voix, "
        "cf. docstring de run_benchmark).",
    )
    args = parser.parse_args()

    result = asyncio.run(
        run_benchmark(
            args.corpus_key,
            args.out_dir,
            args.corpus_dir,
            diarization=not args.no_diarization,
            lan=args.lan,
            target_lang=args.target_lang,
            separation=not args.no_separation,
            translator=args.translator,
        )
    )

    # Rapports séparés "en direct" / "flush final" (2026-07-19, cf. ADR-0044 §Révisions) : un
    # flush final de fin de fichier (`force_final_commit`/`force_final_commit_llm`) n'a pas
    # d'équivalent en usage réel (le flux ne s'arrête jamais) — les mélanger dans le même
    # percentile a produit un p95 bout-en-bout trompeur sur `harness_pipeline_dual.py` (même
    # architecture de commit ici). `.get(..., False)` : rétrocompatible avec d'anciens logs
    # sans le champ `is_final`.
    events = load_events(result.log_path)
    live_events = [e for e in events if not e.get("is_final", False)]
    final_events = [e for e in events if e.get("is_final", False)]

    reports = aggregate_by_stage(live_events)
    end_to_end = aggregate_end_to_end(live_events)
    if end_to_end is not None:
        reports.append(end_to_end)

    print("=== En direct (ce qu'un auditeur entendrait pendant le run) ===")
    print(format_report(reports))

    if final_events:
        final_reports = aggregate_by_stage(final_events)
        final_end_to_end = aggregate_end_to_end(final_events)
        if final_end_to_end is not None:
            final_reports.append(final_end_to_end)
        print("\n=== Flush final de fin de run (pas d'équivalent en usage réel, cf. ADR-0044) ===")
        print(format_report(final_reports))

    print(f"\nLog : {result.log_path}")
    print(f"Transcript : {result.transcript_path}")
    print(f"Audio FR par ligne (un seul fichier continu par ligne) : {result.audio_dir}")


if __name__ == "__main__":
    main()
