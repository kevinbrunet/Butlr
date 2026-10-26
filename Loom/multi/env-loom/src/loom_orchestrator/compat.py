from __future__ import annotations


def patch_wlk_sortformer_state() -> None:
    """Compatibilité WhisperLiveKit / NeMo pour la diarisation Sortformer (ADR-0034).

    ✓ Constaté le 2026-10-26 (corpus b, fedora3) : WhisperLiveKit (0.2.24 comme 0.2.26)
    définit sa propre classe `StreamingSortformerState` sans l'attribut `spkcache_compressed`,
    que NeMo lit dans `sortformer_modules.py` depuis au moins le commit 71820fbc (celui du
    lock). Chaque appel de diarisation levait `AttributeError` (19 fois sur corpus b) et le STT
    prenait ~5 s de retard. NeMo lui-même déclare `spkcache_compressed = False` comme valeur
    par défaut de classe : on fait de même sur la classe de WhisperLiveKit si elle ne l'a pas.
    Sans effet si une version future de WhisperLiveKit ajoute l'attribut.
    """
    from whisperlivekit.diarization import sortformer_backend

    if not hasattr(sortformer_backend.StreamingSortformerState, "spkcache_compressed"):
        sortformer_backend.StreamingSortformerState.spkcache_compressed = False
