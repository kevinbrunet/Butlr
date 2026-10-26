from __future__ import annotations


def hms_to_seconds(hms: str) -> float:
    """Parse un timestamp "H:MM:SS.cc" (centisecondes) en secondes.

    ✓ Format vérifié par lecture directe de `format_time()` dans
    whisperlivekit/timed_objects.py (repo QuentinFuxa/WhisperLiveKit, lu le 2026-07-07) :
    précision au centième de seconde, largement suffisante face au budget WLK de 1000ms.
    """
    hours, minutes, seconds = hms.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
