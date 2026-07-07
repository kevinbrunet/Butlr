from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from types import TracebackType

# Budgets p95 par étage (ms) — cf. budget de latence dans Loom/CLAUDE.md et le backlog.
# Codés en dur à dessein : un dépassement est un WARNING loggué (champ `exceeded`), jamais
# une variabilité silencieusement acceptée (règle transverse "budgets codés en dur").
STAGE_WLK = "wlk"
STAGE_ORCHESTRATOR = "orchestrateur"
STAGE_TTS = "tts"
STAGE_TRANSPORT = "transport"
STAGE_SEAMLESS = "seamless"

# ⚠ STAGE_SEAMLESS : budget provisoire (ADR-0040 retire la traduction de l'étage WLK sans
# établir de nouveau chiffre — la traduction n'y était de toute façon jamais mesurée
# correctement, cf. bug NLLB). 1000ms repris tel quel comme point de départ, à réviser dès
# que ce harnais donne une vraie mesure par tour de parole (T1.2 pour Seamless).
BUDGET_MS: dict[str, int] = {
    STAGE_WLK: 1000,
    STAGE_ORCHESTRATOR: 100,
    STAGE_TTS: 400,
    STAGE_TRANSPORT: 100,
    STAGE_SEAMLESS: 1000,
}


@dataclass(frozen=True)
class LatencyEvent:
    segment_id: str
    stage: str
    t_in: float
    t_out: float
    budget_ms: int
    exceeded: bool

    @classmethod
    def create(cls, segment_id: str, stage: str, t_in: float, t_out: float) -> LatencyEvent:
        if stage not in BUDGET_MS:
            raise ValueError(f"étage inconnu : {stage!r} — attendu un de {sorted(BUDGET_MS)}")
        if t_out < t_in:
            raise ValueError(f"t_out ({t_out}) < t_in ({t_in}) pour le segment {segment_id!r}")

        budget_ms = BUDGET_MS[stage]
        duration_ms = (t_out - t_in) * 1000
        return cls(
            segment_id=segment_id,
            stage=stage,
            t_in=t_in,
            t_out=t_out,
            budget_ms=budget_ms,
            exceeded=duration_ms > budget_ms,
        )


class EventLogger:
    """Écrit les LatencyEvent en JSON lines — un fichier par run de benchmark (T0.3)."""

    def __init__(self, path: Path) -> None:
        self._path = path
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._file = self._path.open("a", encoding="utf-8")

    def log(self, event: LatencyEvent) -> None:
        self._file.write(json.dumps(asdict(event), ensure_ascii=False) + "\n")
        self._file.flush()

    def close(self) -> None:
        self._file.close()

    def __enter__(self) -> EventLogger:
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.close()
