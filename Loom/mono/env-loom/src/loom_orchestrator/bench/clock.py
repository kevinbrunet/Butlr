from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class SampleClock:
    """Horloge unique du benchmark : position d'échantillon dans le flux audio source.

    Toute latence mesurée doit être relative à cette horloge, jamais à l'heure murale du
    process qui mesure (cf. règle transverse "mesurer avant d'optimiser", T0.3 du backlog).
    """

    sample_rate_hz: int

    def elapsed_seconds(self, sample_index: int) -> float:
        if sample_index < 0:
            raise ValueError(f"sample_index négatif : {sample_index}")
        return sample_index / self.sample_rate_hz

    def sample_index(self, elapsed_seconds: float) -> int:
        if elapsed_seconds < 0:
            raise ValueError(f"elapsed_seconds négatif : {elapsed_seconds}")
        return int(elapsed_seconds * self.sample_rate_hz)
