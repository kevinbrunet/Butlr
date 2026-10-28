from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# Plusieurs personnes qui parlent chacune leur tour (pas de chevauchement — ça, c'est multi) :
# chaque segment envoyé à la traduction est attribué à une identité d'après l'empreinte vocale
# (ECAPA-TDNN) de son audio source, et chaque identité reçoit sa propre voix de synthèse.
# Pas de séparation : l'audio est propre, une seule voix à la fois.
#
# ✓ Mesuré le 2026-10-28 (corpus h, 3 lecteurs LibriVox, extraits pris au hasard, similarité
# cosinus entre deux extraits) : même lecteur, médiane 0,46 sur 1,5 s, 0,59 sur 2,5 s ;
# lecteurs différents, médiane ~0,06, max 0,40. En dessous de ~1,5 s les deux distributions
# se recouvrent. On compare chaque segment au centroïde de chaque identité (moyenne de toutes
# ses observations, plus stable qu'un extrait isolé).
# ⚠ Seuils de départ, à ajuster sur les runs.
MATCH_THRESHOLD = 0.35
MIN_EMBED_S = 1.5
# Créer une identité demande plus de preuve que d'en reconnaître une : ✓ run réel corpus h
# (Voxtral, 2026-10-28) — un segment de ~1,6 s (« And what's the use of a book? ») sous le seuil
# créait une identité fantôme. En dessous, un segment qui ne ressemble à personne reste à
# l'identité courante.
NEW_SPEAKER_MIN_S = 2.5
MAX_EMBED_S = 6.0
# Un segment à cheval sur un changement de tour mélange deux voix : son empreinte ne ressemble
# à personne et créait une identité fantôme (✓ simulation corpus h, 2026-10-28 : 4 identités
# pour 3 lecteurs). On ne garde que le plus long passage parlé entre deux pauses d'au moins
# TURN_PAUSE_S.
TURN_PAUSE_S = 0.4

# Voix précalculées Pocket TTS `french_24l` retenues (✓ 2026-10-28, 20 increments FR : 2,7 à
# 5,9 % de mots perdus à la retranscription, durée normale) — les 21 autres sont moins
# intelligibles en français ou anormalement lentes (javert). Choix par hauteur de voix source.
FEMALE_VOICES = ["estelle", "azelma", "jane"]
MALE_VOICES = ["stuart_bell", "daan", "peter_yearsley"]
# Fréquence fondamentale médiane séparant voix graves/aiguës. ✓ Mesuré le 2026-10-28 (corpus
# h, fenêtres de 2,5 s) : lectrice 181-194 Hz en médiane, lecteurs ~110 Hz — mais une fenêtre
# isolée déborde (146 à 239 Hz), d'où une réévaluation unique sur la médiane des
# F0_DECISION_COUNT premières observations.
F0_SPLIT_HZ = 150.0
F0_DECISION_COUNT = 3


def _cosine(a: list[float], b: list[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b))
    na = sum(x * x for x in a) ** 0.5
    nb = sum(y * y for y in b) ** 0.5
    return dot / (na * nb) if na and nb else 0.0


@dataclass
class Speaker:
    ident: int
    voice: str
    centroid: list[float]
    count: int = 1  # observations fiables intégrées au centroïde ; 0 = identité provisoire
    f0s: list[float] = field(default_factory=list)


@dataclass
class SpeakerRegistry:
    """Référentiel ouvert d'identités pour une session — pur, sans modèle."""

    threshold: float = MATCH_THRESHOLD
    speakers: list[Speaker] = field(default_factory=list)
    current: Speaker | None = None

    def identify(
        self, embedding: list[float] | None, duration_s: float, f0_hz: float | None
    ) -> Speaker:
        """Attribue un segment à une identité. `embedding` None ou segment plus court que
        MIN_EMBED_S : empreinte peu fiable, on garde l'identité courante (« Oui, » ne crée pas
        de locuteur) — sauf tout au début, où il faut bien en créer une."""
        reliable = embedding is not None and duration_s >= MIN_EMBED_S
        if not reliable and self.current is not None:
            return self.current
        if reliable and self.current is not None and self.current.count == 0:
            # Identité provisoire (créée sur un premier segment trop court) : elle adopte la
            # première empreinte fiable plutôt que d'en faire naître une seconde.
            self.current.centroid, self.current.count = list(embedding), 1
            self._observe_f0(self.current, f0_hz)
            return self.current
        best, best_sim = None, -1.0
        for speaker in self.speakers:
            sim = _cosine(speaker.centroid, embedding)
            if sim > best_sim:
                best, best_sim = speaker, sim
        if best is not None and best_sim >= self.threshold:
            best.centroid = [
                (c * best.count + e) / (best.count + 1) for c, e in zip(best.centroid, embedding)
            ]
            best.count += 1
            self._observe_f0(best, f0_hz)
            self.current = best
            return best
        if self.current is not None and duration_s < NEW_SPEAKER_MIN_S:
            return self.current
        speaker = Speaker(
            len(self.speakers), self._pick_voice(f0_hz), list(embedding or []), int(reliable)
        )
        if f0_hz is not None:
            speaker.f0s.append(f0_hz)
        self.speakers.append(speaker)
        self.current = speaker
        return speaker

    def _observe_f0(self, speaker: Speaker, f0_hz: float | None) -> None:
        """Réévaluation unique de la voix, à la F0_DECISION_COUNT-ième hauteur mesurée."""
        if f0_hz is None:
            return
        speaker.f0s.append(f0_hz)
        if len(speaker.f0s) != F0_DECISION_COUNT:
            return
        f0s = sorted(speaker.f0s)
        median = f0s[len(f0s) // 2]
        female = median >= F0_SPLIT_HZ
        if (speaker.voice in FEMALE_VOICES) != female:
            speaker.voice = self._pick_voice(median, exclude=speaker)

    def _pick_voice(self, f0_hz: float | None, exclude: Speaker | None = None) -> str:
        female = f0_hz is not None and f0_hz >= F0_SPLIT_HZ
        pool = FEMALE_VOICES if female else MALE_VOICES
        others = [s for s in self.speakers if s is not exclude]
        used = {s.voice for s in others}
        free = [v for v in pool if v not in used]
        return free[0] if free else pool[sum(s.voice in pool for s in others) % len(pool)]


def longest_speech_run(audio: "np.ndarray", sample_rate_hz: int) -> "np.ndarray":
    """Plus long passage de `audio` sans pause d'au moins TURN_PAUSE_S (trames de 20 ms sous
    -35 dB de la plus forte) — pur. Rendu tel quel s'il n'y a pas de telle pause."""
    import numpy as np

    frame = max(1, int(0.02 * sample_rate_hz))
    n = len(audio) // frame
    if n == 0:
        return audio
    rms = np.sqrt(np.mean(audio[: n * frame].reshape(n, frame) ** 2, axis=1))
    speech = rms > rms.max() * 10 ** (-35 / 20)
    min_pause = round(TURN_PAUSE_S / 0.02)
    runs, start, silent = [], None, 0
    for i, v in enumerate(speech):
        if v:
            if start is None:
                start = i
            silent = 0
        elif start is not None:
            silent += 1
            if silent >= min_pause:
                runs.append((start, i - silent + 1))
                start, silent = None, 0
    if start is not None:
        runs.append((start, n - silent))
    if len(runs) <= 1:
        return audio
    a, b = max(runs, key=lambda r: r[1] - r[0])
    return audio[a * frame : b * frame]


class InputAudioHistory:
    """Derniers `keep_s` d'audio d'entrée, adressables en secondes depuis le début du flux
    (même base de temps que les `end` du STT, l'audio étant poussé en temps réel)."""

    def __init__(self, sample_rate_hz: int = 16_000, keep_s: float = 120.0) -> None:
        import numpy as np

        self._sr = sample_rate_hz
        self._keep = int(keep_s * sample_rate_hz)
        self._samples = np.zeros(0, dtype=np.float32)
        self._offset = 0  # index absolu du premier échantillon conservé

    def append(self, pcm: "np.ndarray") -> None:
        import numpy as np

        self._samples = np.concatenate([self._samples, pcm.astype(np.float32)])
        excess = len(self._samples) - self._keep
        if excess > 0:
            self._samples = self._samples[excess:]
            self._offset += excess

    def slice(self, start_s: float, end_s: float) -> "np.ndarray":
        a = max(0, int(start_s * self._sr) - self._offset)
        b = max(0, int(end_s * self._sr) - self._offset)
        return self._samples[a:b]


class SpeakerIdentifier:
    """Empreinte ECAPA + hauteur de voix du segment, puis `SpeakerRegistry`. Les appels GPU
    passent par le consommateur unique (`commit_worker`), comme traduction et synthèse."""

    def __init__(self, device: str = "cuda") -> None:
        from speechbrain.inference.speaker import EncoderClassifier

        self._model = EncoderClassifier.from_hparams(
            source="speechbrain/spkrec-ecapa-voxceleb",
            savedir="pretrained_models/spkrec-ecapa-voxceleb",
            run_opts={"device": device},
        )
        self._device = device
        self.registry = SpeakerRegistry()
        self._last_end_s = 0.0

    def identify_segment(self, history: InputAudioHistory, end_s: float) -> Speaker:
        """Segment source = de la fin du segment précédent à `end_s` (borné à MAX_EMBED_S)."""
        import numpy as np
        import torch

        start_s = max(self._last_end_s, end_s - MAX_EMBED_S)
        self._last_end_s = max(self._last_end_s, end_s)
        audio = longest_speech_run(history.slice(start_s, end_s), 16_000)
        duration_s = len(audio) / 16_000
        embedding, f0_hz = None, None
        if duration_s >= 0.5:
            wav = torch.from_numpy(np.ascontiguousarray(audio)).unsqueeze(0).to(self._device)
            embedding = self._model.encode_batch(wav).squeeze().detach().cpu().tolist()
            f0_hz = _median_f0(audio)
        return self.registry.identify(embedding, duration_s, f0_hz)

    def summary(self) -> str:
        parts = [f"id{s.ident}={s.voice} ({s.count} segments)" for s in self.registry.speakers]
        return f"Locuteurs : {len(parts)} — " + ", ".join(parts)


def _median_f0(audio: "np.ndarray") -> float | None:
    import librosa
    import numpy as np

    f0 = librosa.yin(audio, fmin=60, fmax=400, sr=16_000, frame_length=1024)
    rms = librosa.feature.rms(y=audio, frame_length=1024, hop_length=256)[0][: len(f0)]
    voiced = f0[: len(rms)][rms > rms.max() * 0.1] if rms.size else f0
    return float(np.median(voiced)) if voiced.size else None
