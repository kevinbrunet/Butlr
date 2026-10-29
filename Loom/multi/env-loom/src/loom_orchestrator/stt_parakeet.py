from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

from loom_orchestrator.stt_voxtral import SentenceLines, VoxtralResponse

if TYPE_CHECKING:
    from collections.abc import AsyncIterator

    import numpy as np

# Variante STT Parakeet Multitalker (ADR-0054) : un seul modèle transcrit chaque locuteur
# directement depuis le mélange, guidé par la diarisation Sortformer en streaming — pas de
# séparation de voix, pas d'empreinte, pas de suivi d'identité (ADR-0042/0044 contournés).
#
# ✓ Banc isolé du 2026-10-28 (scratchpad, NeMo 71820fbc, contexte droit 13 = morceaux de
#   1,12 s) : corpus b 50,5 % de mots faux contre 61,8 % (Voxtral) et 67,4 % (WLK) avec la
#   séparation actuelle ; corpus h (3 lecteurs à tour de rôle) 99,2 % des mots attribués au bon
#   lecteur ; corpus a (un lecteur) 1,5 % ; calcul 67 ms par morceau de 1,12 s ; ~6 Go de VRAM.
# ✓ API lue dans `examples/asr/asr_cache_aware_streaming/speech_to_text_multitalker_streaming_
#   infer.py` (NeMo 71820fbc) et `nemo/collections/asr/parts/utils/multispk_transcribe_utils.py`.
ASR_REPO = "nvidia/multitalker-parakeet-streaming-0.6b-v1"
ASR_FILE = "multitalker-parakeet-streaming-0.6b-v1.nemo"
DIAR_REPO = "nvidia/diar_streaming_sortformer_4spk-v2.1"
DIAR_FILE = "diar_streaming_sortformer_4spk-v2.1.nemo"
SAMPLE_RATE_HZ = 16_000
# Contexte droit de l'encodeur, en trames de 80 ms : 13 → morceaux de 14 trames (1,12 s).
# ✓ Banc du 2026-10-28 : 6 (0,56 s) fait 57,8 % de mots faux sur corpus b contre 50,5 %.
DEFAULT_RIGHT_CONTEXT = 13
MAX_SPEAKERS = 4  # limite du diariseur Sortformer `4spk`
# Audio sans nouveau mot d'un locuteur (depuis la fin de son dernier mot) au-delà duquel sa
# ligne active est scellée (rôle d'une fin de tour WLK — déclenche le commit final).
# ✓ Constaté le 2026-10-28 (corpus e/f, pipeline complet) : à 1,0 s, plus court qu'un morceau
# du modèle (1,12 s), tout morceau sans mot nouveau scellait la ligne, souvent juste avant le
# point final émis au morceau suivant : la traduction recevait des miettes (« dedicated »,
# « . ») et son chrF tombait de ~57 à ~41. ⚠ 2,0 s : non calibré au-delà de ce constat.
DEFAULT_PAUSE_SEAL_S = 2.0
# Trames de features (10 ms) gardées avant le premier frame encore à calculer : le calcul par
# tranche (préaccentuation, fenêtre centrée) fausse les toutes premières trames d'une tranche.
_FEATURE_OVERLAP_FRAMES = 4


def valid_feature_frames(n_samples: int, hop: int, n_fft: int) -> int:
    """Nombre de trames de features entièrement déterminées par les `n_samples` premiers
    échantillons (fenêtre centrée de `n_fft`, pas de `hop`) — pur. Une trame i couvre
    [i*hop - n_fft/2, i*hop + n_fft/2)."""
    reach = n_samples - n_fft // 2
    return 0 if reach < 0 else reach // hop + 1


def text_delta(previous: str, current: str) -> str | None:
    """Texte ajouté à la transcription d'un locuteur depuis `previous` — pur. None si le
    texte déjà vu a été révisé (contrat append-only rompu)."""
    if not current.startswith(previous):
        return None
    return current[len(previous) :]


@dataclass
class SpeakerUpdate:
    speaker: int
    delta: str
    end_s: float


class ParakeetMultitalkerEngine:
    """Diarisation Sortformer streaming + Parakeet Multitalker, alimentés au fil de l'eau.
    `push` et `finish` sont bloquants (GPU) : à appeler depuis une seule tâche, via
    `asyncio.to_thread`."""

    def __init__(self, right_context: int = DEFAULT_RIGHT_CONTEXT, device: str = "cuda") -> None:
        import nemo.collections.asr as nemo_asr
        import torch
        from huggingface_hub import hf_hub_download
        from nemo.collections.asr.models.sortformer_diar_models import SortformerEncLabelModel
        from nemo.collections.asr.parts.utils.multispk_transcribe_utils import SpeakerTaggedASR
        from nemo.collections.asr.parts.utils.streaming_utils import (
            CacheAwareStreamingAudioBuffer,
        )
        from omegaconf import OmegaConf, open_dict

        cfg = OmegaConf.create(_multitalker_config(right_context))
        diar = SortformerEncLabelModel.restore_from(
            hf_hub_download(DIAR_REPO, DIAR_FILE), map_location=device
        ).eval()
        diar.streaming_mode = True
        modules = diar.sortformer_modules
        modules.chunk_len = cfg.chunk_len
        modules.spkcache_len = cfg.spkcache_len
        modules.fifo_len = cfg.fifo_len
        modules.chunk_left_context = cfg.chunk_left_context
        modules.chunk_right_context = cfg.chunk_right_context
        modules.spkcache_refresh_rate = cfg.spkcache_refresh_rate
        modules.log = False

        asr = nemo_asr.models.ASRModel.restore_from(hf_hub_download(ASR_REPO, ASR_FILE))
        asr.encoder.set_default_att_context_size(att_context_size=list(cfg.att_context_size))
        # ✓ Constaté le 2026-10-28 (corpus g, pipeline complet) : le décodeur glouton avec
        # graphes CUDA (défaut NeMo) plante (`CUDA error: an illegal memory access`, dans
        # `batched_hyps_to_hypotheses`) quand llama.cpp et Pocket TTS calculent en même temps
        # depuis d'autres threads. Sans graphes, le décodage reste sur les flux CUDA ordinaires.
        decoding_cfg = asr.cfg.decoding
        with open_dict(decoding_cfg):
            decoding_cfg.greedy.use_cuda_graph_decoder = False
        asr.change_decoding_strategy(decoding_cfg, verbose=False)
        self._asr = asr.to(device).eval()
        self._torch = torch
        self._autocast = torch.amp.autocast(device, enabled=True)
        self._buffer = CacheAwareStreamingAudioBuffer(model=self._asr, online_normalization=False)
        self._preprocessor = self._buffer.preprocessor
        featurizer = getattr(self._preprocessor, "featurizer", None)
        self._hop = int(getattr(featurizer, "hop_length", 160))
        self._n_fft = int(getattr(featurizer, "n_fft", 512))
        self._streamer = SpeakerTaggedASR(cfg, self._asr, diar)
        self._streaming_cfg = self._asr.encoder.streaming_cfg
        self._device = device

        import numpy as np

        self._audio = np.zeros(0, dtype=np.float32)
        self._audio_offset = 0  # index absolu du premier échantillon conservé
        self._n_samples = 0
        self._features_done = 0
        self._step = 0
        self._seen_text: dict[int, str] = {}

    @property
    def audio_s(self) -> float:
        """Audio déjà passé au modèle (fin du dernier morceau traité), en secondes."""
        return min(self._buffer.buffer_idx, self._features_done) * 0.01

    def push(self, pcm: "np.ndarray") -> list[SpeakerUpdate]:
        import numpy as np

        self._audio = np.concatenate([self._audio, pcm.astype(np.float32)])
        self._n_samples += len(pcm)
        self._append_features(valid_feature_frames(self._n_samples, self._hop, self._n_fft))
        return self._run_steps(final=False)

    def finish(self) -> list[SpeakerUpdate]:
        """Fin de flux : calcule les dernières trames (fenêtre complétée comme pour un fichier
        entier) et traite le reste, dernier morceau compris."""
        self._append_features(self._n_samples // self._hop + 1)
        return self._run_steps(final=True)

    def _append_features(self, target_frames: int) -> None:
        torch = self._torch
        if target_frames <= self._features_done:
            return
        start_frame = max(0, self._features_done - _FEATURE_OVERLAP_FRAMES)
        start_sample = start_frame * self._hop
        segment = self._audio[start_sample - self._audio_offset :]
        signal = torch.from_numpy(segment).unsqueeze(0).to(self._device)
        length = torch.tensor([len(segment)], device=self._device)
        with torch.inference_mode():
            feats, _ = self._preprocessor(input_signal=signal, length=length)
        first = self._features_done - start_frame
        feats = feats[:, :, first : first + target_frames - self._features_done]
        self._buffer.append_processed_signal(feats, stream_id=0 if self._features_done else -1)
        self._features_done += feats.size(-1)
        # Garde juste ce qu'il faut d'audio pour la prochaine tranche.
        keep_from = max(0, self._features_done - _FEATURE_OVERLAP_FRAMES) * self._hop
        drop = keep_from - self._audio_offset
        if drop > 0:
            self._audio = self._audio[drop:]
            self._audio_offset = keep_from

    def _chunk_frames(self) -> int:
        size = self._streaming_cfg.chunk_size
        if isinstance(size, list):
            return size[0] if self._buffer.buffer_idx == 0 else size[1]
        return size

    def _run_steps(self, final: bool) -> list[SpeakerUpdate]:
        torch = self._torch
        updates: list[SpeakerUpdate] = []
        while self._buffer.buffer is not None and (
            self._buffer.buffer_idx + self._chunk_frames() <= self._features_done
            or (final and self._buffer.buffer_idx < self._features_done)
        ):
            chunk, lengths = next(iter(self._buffer))
            drop = 0 if self._step == 0 else self._streaming_cfg.drop_extra_pre_encoded
            with torch.inference_mode(), self._autocast:
                self._streamer.perform_parallel_streaming_stt_spk(
                    step_num=self._step,
                    chunk_audio=chunk,
                    chunk_lengths=lengths,
                    is_buffer_empty=final and self._buffer.is_buffer_empty(),
                    drop_extra_pre_encoded=drop,
                )
            self._step += 1
            updates.extend(self._collect_updates())
        return updates

    def _collect_updates(self) -> list[SpeakerUpdate]:
        state = self._streamer.instance_manager.batch_asr_states[0]
        updates = []
        for spk, hyp in enumerate(state.previous_hypothesis):
            text = getattr(hyp, "text", None) if hyp is not None else None
            if not isinstance(text, str) or not text:
                continue
            previous = self._seen_text.get(spk, "")
            delta = text_delta(previous, text)
            if delta is None:
                print(f"WARNING Parakeet : texte du locuteur {spk} révisé, ignoré ({text!r}).")
                continue
            self._seen_text[spk] = text
            if not delta:
                continue
            sentences = state._speaker_wise_sentences.get(spk) or []
            end_s = sentences[-1]["end_time"] if sentences else self.audio_s
            # ✓ Constaté le 2026-10-28 : l'horodatage de NeMo peut dépasser l'audio déjà reçu
            # (contexte droit) — borné, sinon la latence mesurée devient négative.
            updates.append(SpeakerUpdate(spk, delta, min(float(end_s), self.audio_s)))
        return updates


def _multitalker_config(right_context: int) -> dict:
    """Valeurs par défaut de `MultitalkerTranscriptionConfig` (script d'exemple NeMo
    71820fbc), réduites à ce que lit `SpeakerTaggedASR`."""
    return {
        "max_num_of_spks": MAX_SPEAKERS,
        "parallel_speaker_strategy": True,
        "masked_asr": True,
        "mask_preencode": False,
        "cache_gating": True,
        "cache_gating_buffer_size": 2,
        "single_speaker_mode": False,
        "feat_len_sec": 0.01,
        "streaming_mode": True,
        "spkcache_len": 188,
        "spkcache_refresh_rate": 0,
        "fifo_len": 188,
        "chunk_len": 0,
        "chunk_left_context": 0,
        "chunk_right_context": 0,
        "att_context_size": [70, right_context],
        "use_amp": True,
        "batch_size": 1,
        "online_normalization": False,
        "pad_and_drop_preencoded": False,
        "generate_realtime_scripts": False,
        "spk_supervision": "diar",
        "binary_diar_preds": False,
        "verbose": False,
        "word_window": 50,
        "sent_break_sec": 0.5,
        "fix_prev_words_count": 5,
        "update_prev_words_sentence": 5,
        "left_frame_shift": -1,
        "right_frame_shift": 0,
        "min_sigmoid_val": 1e-2,
        "discarded_frames": 8,
        "print_time": False,
        "print_sample_indices": [0],
        "colored_text": False,
        "real_time_mode": False,
        "print_path": None,
        "ignored_initial_frame_steps": 5,
        "finetune_realtime_ratio": 0.01,
        "log": False,
        "deploy_mode": True,
    }


@dataclass
class ParakeetSpeakerView:
    """Un locuteur de Parakeet vu comme un `AudioProcessor` WLK par l'orchestrateur
    (`create_tasks()` → réponses `{"lines": [...]}`, `process_audio`) — l'audio, lui, passe
    par le moteur unique (`ParakeetMultitalkerEngine`), pas par cette vue."""

    speaker: str
    pause_seal_s: float = DEFAULT_PAUSE_SEAL_S
    _lines: SentenceLines = field(init=False)
    _responses: asyncio.Queue = field(default_factory=asyncio.Queue)
    _last_text_s: float = 0.0
    _closed: bool = False

    def __post_init__(self) -> None:
        self._lines = SentenceLines(speaker=self.speaker)

    async def create_tasks(self) -> "AsyncIterator[VoxtralResponse]":
        return self._iter_responses()

    async def _iter_responses(self) -> "AsyncIterator[VoxtralResponse]":
        while True:
            item = await self._responses.get()
            if item is None:
                return
            yield item

    async def process_audio(self, pcm16: bytes) -> None:
        """Sans effet : l'audio va au moteur. Présent pour l'interface commune."""

    def apply(self, delta: str, end_s: float) -> None:
        self._lines.append(delta, end_s)
        if delta.strip():
            self._last_text_s = end_s
        self._emit()

    def seal_if_paused(self, now_s: float) -> None:
        if now_s - self._last_text_s > self.pause_seal_s and self._lines.seal_active():
            self._emit()

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._lines.seal_active()
        self._emit()
        self._responses.put_nowait(None)

    def _emit(self) -> None:
        self._responses.put_nowait(VoxtralResponse(self._lines.snapshot()))
