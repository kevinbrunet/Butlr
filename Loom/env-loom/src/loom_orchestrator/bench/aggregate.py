from __future__ import annotations

import argparse
import json
import statistics
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

from loom_orchestrator.bench.instrumentation import BUDGET_MS


@dataclass(frozen=True)
class StageReport:
    stage: str
    count: int
    p50_ms: float
    p95_ms: float
    budget_ms: int
    exceeded_count: int

    @property
    def exceeded_ratio(self) -> float:
        return self.exceeded_count / self.count if self.count else 0.0


def load_events(path: Path) -> list[dict]:
    events = []
    with path.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                events.append(json.loads(line))
    return events


def _percentiles_ms(durations_ms: list[float]) -> tuple[float, float]:
    if not durations_ms:
        raise ValueError("aucune durée à agréger")
    if len(durations_ms) == 1:
        return durations_ms[0], durations_ms[0]
    # n=100 découpe en centiles : quantiles[49] = 50e percentile, quantiles[94] = 95e.
    quantiles = statistics.quantiles(durations_ms, n=100, method="inclusive")
    return quantiles[49], quantiles[94]


def aggregate_by_stage(events: list[dict]) -> list[StageReport]:
    by_stage: dict[str, list[dict]] = defaultdict(list)
    for event in events:
        by_stage[event["stage"]].append(event)

    reports = []
    for stage, stage_events in sorted(by_stage.items()):
        durations_ms = [(e["t_out"] - e["t_in"]) * 1000 for e in stage_events]
        p50, p95 = _percentiles_ms(durations_ms)
        reports.append(
            StageReport(
                stage=stage,
                count=len(stage_events),
                p50_ms=p50,
                p95_ms=p95,
                budget_ms=stage_events[0]["budget_ms"],
                exceeded_count=sum(1 for e in stage_events if e["exceeded"]),
            )
        )
    return reports


def aggregate_end_to_end(events: list[dict]) -> StageReport | None:
    """Latence bout-en-bout par segment_id : de t_in du premier étage à t_out du dernier.

    Le budget bout-en-bout est la somme des budgets des étages réellement présents dans le
    log — tant que seul WLK tourne (Phase 0/1), c'est juste le budget WLK, pas les 1600ms
    du budget complet à 4 étages.
    """
    by_segment: dict[str, list[dict]] = defaultdict(list)
    for event in events:
        by_segment[event["segment_id"]].append(event)

    durations_ms = []
    for segment_events in by_segment.values():
        t_in = min(e["t_in"] for e in segment_events)
        t_out = max(e["t_out"] for e in segment_events)
        durations_ms.append((t_out - t_in) * 1000)

    if not durations_ms:
        return None

    stages_present = {e["stage"] for e in events}
    total_budget_ms = sum(BUDGET_MS[s] for s in stages_present)

    p50, p95 = _percentiles_ms(durations_ms)
    return StageReport(
        stage="bout-en-bout",
        count=len(durations_ms),
        p50_ms=p50,
        p95_ms=p95,
        budget_ms=total_budget_ms,
        exceeded_count=sum(1 for d in durations_ms if d > total_budget_ms),
    )


def format_report(reports: list[StageReport]) -> str:
    header = f"{'étage':<15} {'n':>5} {'p50 (ms)':>10} {'p95 (ms)':>10} {'budget (ms)':>12} {'dépassements':>13}"
    lines = [header]
    for r in reports:
        lines.append(
            f"{r.stage:<15} {r.count:>5} {r.p50_ms:>10.1f} {r.p95_ms:>10.1f} "
            f"{r.budget_ms:>12} {r.exceeded_count:>13}"
        )
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Agrège les latences p50/p95 d'un run de benchmark Loom (T0.3)."
    )
    parser.add_argument("log_path", type=Path, help="Fichier JSON lines produit par EventLogger")
    args = parser.parse_args()

    events = load_events(args.log_path)
    reports = aggregate_by_stage(events)
    end_to_end = aggregate_end_to_end(events)
    if end_to_end is not None:
        reports.append(end_to_end)

    print(format_report(reports))


if __name__ == "__main__":
    main()
