from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

SAMPLE_RATE_HZ = 16_000
# ✓ speechbrain/sepformer-whamr attend du 8kHz mono (vérifié par documentation officielle
# et par le code d'exemple, lu le 2026-07-15, pas exécuté sur cette machine — pas la cible).
SEPFORMER_SAMPLE_RATE_HZ = 8_000

DEFAULT_SEPARATOR_SOURCE = "speechbrain/sepformer-whamr"
DEFAULT_EMBEDDER_SOURCE = "speechbrain/spkrec-ecapa-voxceleb"


class VoiceSeparator:
    """Sépare un mélange 2 locuteurs en 2 flux distincts — SepFormer-WHAMR (ADR-0042).

    ⚠ API vérifiée par documentation officielle SpeechBrain (lue le 2026-07-15, pas exécutée
    sur cette machine — pas la cible) : `SepformerSeparation.separate_batch(mix)` où `mix`
    est un tenseur `[batch, temps]`, retourne `[batch, temps, n_sources]` (ici 2). Pas de
    méthode fichier utilisée ici (`separate_file` existe aussi mais prend un chemin, pas un
    tableau en mémoire — pas adapté à notre audio déjà chargé).

    ⚠ Coût quadratique en durée (attention du transformer dual-path, cf. ADR-0042) —
    l'appelant est responsable de borner la durée de `audio` (jamais la ligne WLK entière,
    cf. le problème déjà rencontré avec un monologue continu sur le corpus `a`). Cette
    classe ne borne rien elle-même.
    """

    def __init__(self, source: str = DEFAULT_SEPARATOR_SOURCE, device: str = "cuda") -> None:
        from speechbrain.inference.separation import SepformerSeparation

        savedir = f"pretrained_models/{source.rsplit('/', 1)[-1]}"
        self._model = SepformerSeparation.from_hparams(
            source=source, savedir=savedir, run_opts={"device": device}
        )
        self._device = device

    def separate(self, audio: "np.ndarray") -> list["np.ndarray"]:
        """Sépare `audio` (16kHz mono) en 2 flux (16kHz mono chacun) — ré-échantillonnage
        vers 8kHz avant séparation et retour à 16kHz après, pour rester compatible avec le
        reste du pipeline (AlignAtt/Seamless, `read_segment`, tous en 16kHz).
        """
        import torch
        import torchaudio

        mix = torch.from_numpy(audio).float().unsqueeze(0).to(self._device)
        mix_8k = torchaudio.functional.resample(mix, SAMPLE_RATE_HZ, SEPFORMER_SAMPLE_RATE_HZ)
        est_sources = self._model.separate_batch(mix_8k)  # [1, temps_8k, n_sources]

        streams: list[np.ndarray] = []
        for i in range(est_sources.shape[-1]):
            stream_8k = est_sources[:, :, i]
            stream_16k = torchaudio.functional.resample(
                stream_8k, SEPFORMER_SAMPLE_RATE_HZ, SAMPLE_RATE_HZ
            )
            streams.append(stream_16k.squeeze(0).detach().cpu().numpy())
        return streams


class SpeakerEmbedder:
    """Extrait un embedding de locuteur — ECAPA-TDNN (ADR-0042). Vecteur de taille fixe,
    indépendant de la durée/du contenu (pooling statistique), comparable par similarité
    cosinus (cf. `speaker_tracking.cosine_similarity`).

    ⚠ API vérifiée par documentation officielle SpeechBrain (lue le 2026-07-15, pas exécutée
    sur cette machine) : `EncoderClassifier.encode_batch(wavs)`, attend du **16kHz** (pas
    8kHz comme SepFormer-WHAMR — pas de ré-échantillonnage nécessaire ici, contrairement à
    `VoiceSeparator`).
    """

    def __init__(self, source: str = DEFAULT_EMBEDDER_SOURCE, device: str = "cuda") -> None:
        from speechbrain.inference.speaker import EncoderClassifier

        savedir = f"pretrained_models/{source.rsplit('/', 1)[-1]}"
        self._model = EncoderClassifier.from_hparams(
            source=source, savedir=savedir, run_opts={"device": device}
        )
        self._device = device

    def embed(self, audio: "np.ndarray") -> list[float]:
        import torch

        wav = torch.from_numpy(audio).float().unsqueeze(0).to(self._device)
        embedding = self._model.encode_batch(wav)
        return embedding.squeeze().detach().cpu().tolist()
