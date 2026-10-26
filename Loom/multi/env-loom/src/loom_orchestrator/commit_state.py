from __future__ import annotations

import time
from dataclasses import dataclass, field

from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ⚠ Pas encore benchmarké (ADR-0041) — même valeur que `bench/harness_pipeline.py`, reprise
# ici pour le défaut de `LineCommitState.last_alignatt_end_s` (évite de ré-encoder
# l'intégralité de l'audio d'une ligne à chaque mise à jour WLK).
MIN_NEW_AUDIO_S = 1.0


@dataclass
class LineCommitState:
    """État de commit AlignAtt + suivi d'identité par embedding pour une ligne WLK — cf.
    ADR-0041 (commit) et ADR-0042 (embedding).

    Relocalisé depuis `bench/harness_pipeline.py` (2026-07-19, ADR-0045) : `main.py`
    (orchestrateur réel) a besoin de ce même état, et du code de production important depuis
    `bench/` (documenté dans `Loom/CLAUDE.md` comme outillage de mesure, "non exécutable/
    testable hors machine cible") était un sens de dépendance à l'envers — resté inoffensif
    tant que seul `bench/` importait du `bench/`.
    """

    committed_fr: str = ""
    last_alignatt_end_s: float = -MIN_NEW_AUDIO_S
    chunk_count: int = 0
    audio_chunks: list = field(default_factory=list)
    embedding: list | None = None
    embedding_count: int = 0
    flushed_source: str = ""


def _release_gpu_state(state: LineCommitState) -> None:
    """Vide `audio_chunks` une fois une ligne définitivement scellée — plus jamais réutilisé
    après ce point, donc pas de raison de le garder en mémoire.

    ⚠ Constaté par exécution réelle (2026-07-15, cf. Révisions ADR-0042) : fuite mémoire GPU
    observée sur un run réel — 13,9 Go → 29,85 Go de VRAM utilisée en 30s (`nvidia-smi`),
    utilisation GPU restée basse (17-21%) pendant ce temps, signe d'une accumulation plutôt
    que d'un calcul intense. À l'époque imputé à `state.voice_state` (état de continuation
    TTS, jamais nettoyé après scellement d'une ligne) — ce champ a disparu depuis (cf.
    Révisions ADR-0041, 2026-07-25 : abandon de `synthesize_continuation`) ; `empty_cache()`
    conservé par hygiène générale, pas revérifié comme suffisant seul.
    """
    state.audio_chunks = []

    import torch

    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def _consume_stream(
    synth: PocketTtsSynthesizer, text: str, voice_state: object | None = None
) -> tuple[list, float]:
    """Épuise `synthesize_stream` en thread (bloquant/CPU, cf. règle transverse) et mesure le
    délai jusqu'au premier chunk (TTFC, la métrique de budget de ADR-0036) — pas le temps
    total de synthèse de l'increment.

    `voice_state` : voix à utiliser pour cet increment (pool de repli ou profil personnalisé
    résolu par `voice_personalization.PersonalizedVoiceManager.get_voice_state`, ADR-0046) —
    `None` retombe sur la voix de repli du constructeur (cf. `synthesize_stream`).

    ⚠ Chaque appel repart de l'état vocal fourni (`copy_state=True`, le défaut Pocket TTS) —
    pas de continuité prosodique entre les increments d'une même ligne (cf. Révisions
    ADR-0041, 2026-07-25 : `synthesize_continuation`/`copy_state=False` abandonné, fait
    dégénérer Pocket TTS en boucle audio). Rupture de prosodie/débit à chaque frontière
    d'increment assumée pour l'instant — pas encore de mitigation (crossfade au mixage,
    increments plus longs) mise en place.
    """
    t0 = time.monotonic()
    chunks = []
    ttfc_s: float | None = None
    for chunk in synth.synthesize_stream(text, voice_state):
        if ttfc_s is None:
            ttfc_s = time.monotonic() - t0
        chunks.append(chunk)
    if ttfc_s is None:
        ttfc_s = time.monotonic() - t0
    return chunks, ttfc_s
