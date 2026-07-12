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


PYANNOTE_SAMPLE_RATE_HZ = 16_000
# ✓ Fenêtre fixe imposée par le modèle (80 000 échantillons = 5s à 16kHz), pas un choix —
# vérifié par lecture de la fiche modèle HF (2026-07-17). Contrairement à VoiceSeparator
# (SepFormer), pas de marge sur la durée : l'appelant doit fournir au plus cette taille.
PYANNOTE_CHUNK_SAMPLES = 80_000
DEFAULT_PYANNOTE_SOURCE = "pyannote/separation-ami-1.0"


class PyannoteVoiceSeparator:
    """Sépare un mélange en jusqu'à 3 flux — `pyannote/separation-ami-1.0` (ADR-0044,
    2026-07-17), alternative à `VoiceSeparator` (SepFormer-WHAMR) après confirmation
    empirique que ce dernier sépare mal même un enregistrement réel homogène (cf. Révisions
    ADR-0044, `corpus g` — un flux séparé "carrément inaudible" selon Kevin). Entraîné
    directement sur AMI-SDM (audio réel de réunion, micro unique) via MixIT+PIT, contrairement
    à SepFormer-WHAMR (mix synthétiques WSJ0+WHAM) — meilleure adéquation de domaine attendue,
    ⚠ pas encore confirmée à l'oreille sur notre propre corpus.

    ⚠ Modèle à accès conditionnel sur Hugging Face (gated) — accepter les conditions de
    `pyannote/separation-ami-1.0` **et** `pyannote/speech-separation-ami-1.0` sur
    huggingface.co, puis exporter un token d'accès dans la variable d'environnement
    `HF_TOKEN`. Jamais committé (cf. `Butlr/CLAUDE.md`, pas de secrets en git).

    On appelle ici le modèle de base directement (`Model.from_pretrained`), un forward par
    fenêtre fixe de 5s — pas le `Pipeline` complet (`pyannote/speech-separation-ami-1.0`), qui
    lui découpe/recolle un fichier entier avec un pas de 500ms (~10 appels par 5s) et viserait
    plutôt un traitement hors-ligne fichier entier, incompatible avec notre budget de latence.
    """

    def __init__(self, source: str = DEFAULT_PYANNOTE_SOURCE, device: str = "cuda") -> None:
        import os

        from pyannote.audio import Model

        token = os.environ.get("HF_TOKEN")
        if not token:
            raise RuntimeError(
                "HF_TOKEN manquant — pyannote/separation-ami-1.0 est un modèle à accès "
                "conditionnel (gated) : accepter ses conditions sur huggingface.co (et celles "
                "de pyannote/speech-separation-ami-1.0) puis exporter un token d'accès dans "
                "HF_TOKEN avant d'instancier PyannoteVoiceSeparator."
            )
        self._model = Model.from_pretrained(source, use_auth_token=token)
        self._model.to(device)
        self._device = device

    # ⚠ Seuils non calibrés empiriquement — valeurs de départ. `DIARIZATION_ACTIVITY_THRESHOLD`
    # suppose que `self.activation[0]` (ToTaToNet.py, pyannote-audio) produit une probabilité
    # d'activité *par locuteur indépendante* (type sigmoïde) — pas vérifié par exécution
    # réelle si c'est plutôt une softmax (auquel cas la logique "compter les locuteurs actifs
    # simultanément" serait fausse, une softmax à 3 sorties ne dépasse presque jamais 0.5 pour
    # 2 locuteurs à la fois). `DIARIZATION_MIN_OVERLAP_FRAMES` : au moins ~100ms de
    # chevauchement simultané pour compter comme un vrai chevauchement, pas juste un artefact
    # d'un ou deux frames — à 624 frames / 5s (~125 fps, cf. fiche modèle), ~100ms ≈ 12 frames.
    DIARIZATION_ACTIVITY_THRESHOLD = 0.5
    DIARIZATION_MIN_OVERLAP_FRAMES = 12

    def separate_and_detect_overlap(
        self, audio: "np.ndarray"
    ) -> tuple[list["np.ndarray"], bool]:
        """Comme `separate`, mais retourne aussi `has_overlap` — dérivé de la diarisation
        **native** du modèle (conjointement entraînée avec la séparation, cf. `ToTaToNet.py`
        dans pyannote-audio), pas d'une vérification a posteriori par embedding ECAPA (cf.
        `speaker_tracking.streams_are_distinct`, qui reste nécessaire pour SepFormer — sans
        signal de diarisation natif — mais n'a plus de raison d'être ici, ADR-0044 Révisions
        2026-07-18, remarque de Kevin : ECAPA doit servir uniquement au suivi d'identité dans
        le temps, pas à juger si la séparation elle-même avait un sens).

        Un seul appel au modèle pour les deux informations (diarisation et flux séparés
        partagent le même passage encodeur+masker, cf. `ToTaToNet.forward`) — pas de coût
        supplémentaire à calculer les deux plutôt qu'un seul.
        """
        import numpy as np
        import torch

        if len(audio) > PYANNOTE_CHUNK_SAMPLES:
            raise ValueError(
                f"audio de {len(audio)} échantillons > {PYANNOTE_CHUNK_SAMPLES} attendus par "
                "pyannote/separation-ami-1.0 (fenêtre fixe de 5s) — tronquer avant l'appel."
            )
        padded = np.zeros(PYANNOTE_CHUNK_SAMPLES, dtype=np.float32)
        padded[: len(audio)] = audio

        waveform = torch.from_numpy(padded).float().unsqueeze(0).unsqueeze(0).to(self._device)
        with torch.inference_mode():
            diarization, sources = self._model(waveform)
        # sources : (batch=1, échantillons, locuteurs) — cf. fiche modèle HF.
        # diarization : (batch=1, frames, locuteurs) — probabilité d'activité par locuteur.
        n_speakers = sources.shape[-1]
        streams = [sources[0, : len(audio), i].detach().cpu().numpy() for i in range(n_speakers)]

        active_counts = (diarization[0] > self.DIARIZATION_ACTIVITY_THRESHOLD).sum(dim=-1)
        overlap_frames = int((active_counts >= 2).sum().item())
        has_overlap = overlap_frames >= self.DIARIZATION_MIN_OVERLAP_FRAMES
        print(
            f"DEBUG pyannote diarization: frames_chevauchement={overlap_frames}/"
            f"{diarization.shape[1]} has_overlap={has_overlap}"
        )
        return streams, has_overlap

    def separate(self, audio: "np.ndarray") -> list["np.ndarray"]:
        """Sépare `audio` (16kHz mono, doit faire au plus `PYANNOTE_CHUNK_SAMPLES` — padding
        silence si plus court) en jusqu'à 3 flux (16kHz mono chacun, tronqués à la longueur
        de `audio`, le padding retiré avant de retourner). Équivalent à
        `separate_and_detect_overlap` sans la diarisation — gardé pour compatibilité
        d'interface avec `VoiceSeparator` (SepFormer, `harness_separation.py`)."""
        streams, _has_overlap = self.separate_and_detect_overlap(audio)
        return streams
