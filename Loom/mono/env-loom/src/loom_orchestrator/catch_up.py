from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

# Rattrapage du retard de sortie : le français synthétisé dure plus longtemps que l'anglais
# source, et le mixage ne jette plus rien (cf. `audio_mixer.DEFAULT_MAX_BUFFER_S`) — la sortie
# prend donc du retard. Chaque increment est entièrement synthétisé avant d'être poussé au
# mixage (`_consume_stream`), on peut donc le retoucher en entier sans ajouter de latence.
# Deux étages, dans l'ordre :
#   1. retard > LATE_S : on coupe les silences (début/fin d'increment ramenés à
#      EDGE_PAUSE_S, pauses internes plafonnées à INNER_PAUSE_S) — sans déformer la voix ;
#   2. retard restant (déduction faite du silence coupé) > SPEED_FROM_S : on accélère la
#      lecture sans changer la hauteur (WSOLA), linéairement jusqu'à MAX_RATE.
# ⚠ Seuils non calibrés (2026-10-28) : premier jet, à ajuster à l'écoute (ADR-0053).
LATE_S = 0.5
EDGE_PAUSE_S = 0.1
INNER_PAUSE_S = 0.25
SPEED_FROM_S = 1.5
SPEED_FULL_S = 6.0
MAX_RATE = 1.25

FRAME_S = 0.02
# Seuil de silence relatif à la trame la plus forte de l'increment (robuste au niveau de voix).
SILENCE_DB = -40.0


def speech_mask(audio: "np.ndarray", sample_rate_hz: int) -> "np.ndarray":
    """Une valeur par trame de FRAME_S : True si la trame contient de la parole — pur."""
    import numpy as np

    frame = max(1, int(FRAME_S * sample_rate_hz))
    n = len(audio) // frame
    if n == 0:
        return np.zeros(0, dtype=bool)
    rms = np.sqrt(np.mean(audio[: n * frame].reshape(n, frame) ** 2, axis=1))
    peak = rms.max()
    if peak <= 0:
        return np.zeros(n, dtype=bool)
    return rms > peak * 10 ** (SILENCE_DB / 20)


def trim_silences(audio: "np.ndarray", sample_rate_hz: int) -> "np.ndarray":
    """Ramène le silence de début/fin à EDGE_PAUSE_S et plafonne chaque pause interne à
    INNER_PAUSE_S — pur. Un increment sans parole détectée est rendu tel quel."""
    import numpy as np

    frame = max(1, int(FRAME_S * sample_rate_hz))
    mask = speech_mask(audio, sample_rate_hz)
    if not mask.any():
        return audio
    edge, inner = round(EDGE_PAUSE_S / FRAME_S), round(INNER_PAUSE_S / FRAME_S)
    keep = np.zeros(len(mask), dtype=bool)
    speech = np.flatnonzero(mask)
    first, last = speech[0], speech[-1]
    keep[max(0, first - edge) : last + 1 + edge] = True
    # Pauses internes : on garde la moitié du plafond de part et d'autre (raccords doux).
    gap_start = None
    for i in range(first, last + 1):
        if not mask[i] and gap_start is None:
            gap_start = i
        elif mask[i] and gap_start is not None:
            if i - gap_start > inner:
                keep[gap_start + inner // 2 : i - (inner - inner // 2)] = False
            gap_start = None
    samples = np.repeat(keep, frame)
    tail = audio[len(samples) :] if keep[-1] else audio[:0]
    return np.concatenate([audio[: len(samples)][samples], tail])


def rate_for_backlog(backlog_s: float) -> float:
    """Vitesse de lecture pour un retard donné (après coupe des silences) — pur."""
    if backlog_s <= SPEED_FROM_S:
        return 1.0
    ramp = min(1.0, (backlog_s - SPEED_FROM_S) / (SPEED_FULL_S - SPEED_FROM_S))
    return 1.0 + ramp * (MAX_RATE - 1.0)


def time_stretch(audio: "np.ndarray", rate: float, sample_rate_hz: int) -> "np.ndarray":
    """Accélère (`rate` > 1) sans changer la hauteur, par WSOLA (recouvrement-addition avec
    recherche de la meilleure similarité de forme d'onde) — pur. Trames de 40 ms."""
    import numpy as np

    if rate == 1.0 or len(audio) == 0:
        return audio
    n = int(0.04 * sample_rate_hz) // 2 * 2
    hop_out = n // 2
    tol = hop_out // 2
    hop_in = hop_out * rate
    out_len = int(len(audio) / rate)
    x = np.pad(audio.astype(np.float32), (tol, 2 * n + tol + int(hop_in)))
    window = np.hanning(n).astype(np.float32)
    y = np.zeros(out_len + n, dtype=np.float32)
    norm = np.zeros(out_len + n, dtype=np.float32)
    prev = None
    k = 0
    while k * hop_out < out_len:
        nominal = int(k * hop_in) + tol
        if prev is None:
            best = nominal
        else:
            natural = x[prev + hop_out : prev + hop_out + n]
            region = x[nominal - tol : nominal + tol + n]
            best = nominal - tol + int(np.argmax(np.correlate(region, natural, "valid")))
        start = k * hop_out
        y[start : start + n] += x[best : best + n] * window
        norm[start : start + n] += window
        prev = best
        k += 1
    return y[:out_len] / np.maximum(norm[:out_len], 1e-3)


class CatchUp:
    """Applique le rattrapage à chaque increment selon le retard courant de son identité,
    et cumule ce qui a été gagné (pour le bilan de fin de run)."""

    def __init__(self, sample_rate_hz: int) -> None:
        self._sample_rate_hz = sample_rate_hz
        self.silence_cut_s = 0.0
        self.speedup_saved_s = 0.0
        self.max_rate = 1.0

    def process(self, chunks: "list[np.ndarray]", backlog_s: float) -> "np.ndarray":
        """`chunks` : l'increment complet tel que produit par la synthèse ; `backlog_s` :
        audio encore en attente de lecture pour cette identité au moment de le pousser."""
        import numpy as np

        sr = self._sample_rate_hz
        audio = np.concatenate([np.asarray(c, dtype=np.float32).reshape(-1) for c in chunks])
        if backlog_s <= LATE_S:
            return audio
        trimmed = trim_silences(audio, sr)
        cut_s = (len(audio) - len(trimmed)) / sr
        self.silence_cut_s += cut_s
        rate = rate_for_backlog(backlog_s - cut_s)
        if rate == 1.0:
            return trimmed
        stretched = time_stretch(trimmed, rate, sr)
        self.speedup_saved_s += (len(trimmed) - len(stretched)) / sr
        self.max_rate = max(self.max_rate, rate)
        return stretched

    def summary(self) -> str:
        return (
            f"Rattrapage : {self.silence_cut_s:.1f}s de silence coupées, "
            f"{self.speedup_saved_s:.1f}s gagnées par accélération (vitesse max x{self.max_rate:.2f})"
        )
