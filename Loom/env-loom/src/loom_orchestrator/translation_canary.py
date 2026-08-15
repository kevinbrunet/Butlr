from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

SAMPLE_RATE_HZ = 16_000
MODEL_NAME = "nvidia/canary-1b-v2"

# ⚠ ADR-0047 : Canary-1B-v2 ne couvre que 25 langues européennes — pas le mandarin. Le
# chinois est explicitement hors périmètre pour ce traducteur (cf. ADR-0047 §Context),
# contrairement à `translation_seamless.LANGUAGE_CODES`/`translation_llm` qui couvraient
# EN/ZH→FR. Ne pas ajouter "zh" ici sans relire l'ADR d'abord.
SUPPORTED_SOURCE_LANGUAGES = frozenset({"en", "fr"})

# Paramètres de la politique de décodage streaming native NeMo (`AEDStreamingDecodingConfig`,
# policy="alignatt") — ✓ noms et valeurs par défaut lus dans la doc officielle NeMo ("Canary
# Chunked and Streaming Decoding", 2026-08-15), cf. ADR-0047 §Context. ⚠ Jamais exécutés :
# aucune confirmation par run réel que ces valeurs sont adaptées à notre cas (parole continue,
# cible p95 1,5-2s) — point de départ documenté, pas calibré empiriquement (même statut que
# `FRONTIER_FRAMES_DEFAULT` dans `alignatt.py` pour Seamless).
CHUNK_SECS_DEFAULT = 2.0
LEFT_CONTEXT_SECS_DEFAULT = 10.0
RIGHT_CONTEXT_SECS_DEFAULT = 2.0
ALIGNATT_THR_DEFAULT = 8
XATT_SCORES_LAYER_DEFAULT = -2


class AlignAttCanaryTranslator:
    """Traduction speech-to-text EN→FR via `nvidia/canary-1b-v2` (NeMo), politique de
    décodage streaming AlignAtt **native** à NeMo (`AEDStreamingDecodingConfig`,
    `policy="alignatt"`) — cf. ADR-0047.

    Contrairement à `AlignAttSeamlessTranslator` (ADR-0041), qui ré-implémente AlignAtt à la
    main contre les tenseurs d'attention bruts de `transformers` (parce qu'aucune politique de
    streaming n'existait pour Seamless), ici NeMo porte la logique de frontière directement :
    ce module ne fait qu'appeler l'API NeMo documentée avec les bons paramètres, pas de calcul
    de frontière ici (`loom_orchestrator/alignatt.py` n'est pas utilisé sur ce chemin).

    ⚠ **Aucune ligne de ce module n'a été exécutée** — écrit contre la documentation officielle
    NeMo ("Canary Chunked and Streaming Decoding", lue le 2026-08-15) et le script d'exemple
    `examples/asr/asr_chunked_inference/aed/speech_to_text_aed_streaming_infer.py` du dépôt
    NVIDIA-NeMo/NeMo, jamais contre une exécution réelle ni une lecture du code source NeMo. À
    vérifier/corriger au premier run sur la machine cible — même statut que
    `speaker_separation.py` (ADR-0042) à sa création.

    ⚠ Bug connu non résolu au 2026-08-15 (`NVIDIA-NeMo/NeMo#15231`) : le décodage streaming
    AlignAtt sur `canary-1b-v2` se bloquerait après ~20-40s sur de l'audio continu long. Premier
    test à faire avant toute autre validation (cf. ADR-0047 §Consequences) — pas contourné ici.
    """

    def __init__(
        self,
        model_name: str = MODEL_NAME,
        chunk_secs: float = CHUNK_SECS_DEFAULT,
        left_context_secs: float = LEFT_CONTEXT_SECS_DEFAULT,
        right_context_secs: float = RIGHT_CONTEXT_SECS_DEFAULT,
        alignatt_thr: int = ALIGNATT_THR_DEFAULT,
        xatt_scores_layer: int = XATT_SCORES_LAYER_DEFAULT,
    ) -> None:
        # ⚠ Import différé (comme les autres traducteurs) : `nemo_toolkit['asr']` est une
        # dépendance lourde, neuve dans `env-loom` (jamais installée avant ADR-0047) — pas de
        # garantie qu'elle coexiste sans conflit avec les versions torch/torchaudio déjà
        # pinnées par WhisperLiveKit/SpeechBrain/`llama-cpp-python` (cf. ADR-0047 §Consequences).
        from nemo.collections.asr.models import EncDecMultiTaskModel

        # ⚠ Nom de classe/paramètres du constructeur de la politique de streaming non confirmés
        # par exécution — `AEDStreamingDecodingConfig` d'après la doc NeMo, chemin d'import
        # exact (`nemo.collections.asr.parts.submodules...` ou équivalent) à corriger au
        # premier run si celui-ci échoue.
        from nemo.collections.asr.parts.submodules.multitask_decoding import (
            AEDStreamingDecodingConfig,
        )

        self._model = EncDecMultiTaskModel.from_pretrained(model_name)
        self._streaming_cfg = AEDStreamingDecodingConfig(
            policy="alignatt",
            chunk_secs=chunk_secs,
            left_context_secs=left_context_secs,
            right_context_secs=right_context_secs,
            alignatt_thr=alignatt_thr,
            xatt_scores_layer=xatt_scores_layer,
        )
        self._model.change_decoding_strategy(self._streaming_cfg)

    def translate(self, audio: "np.ndarray", target_lang: str = "fr") -> str:
        """Traduit un segment audio 16kHz mono complet (pas de streaming — un seul appel,
        pour comparaison avec `SeamlessTranslator.translate`/`LlmTranslator`). `target_lang`
        doit être "fr" (seule direction validée par ADR-0047) ; le chinois n'est pas supporté
        par ce modèle (cf. `SUPPORTED_SOURCE_LANGUAGES`)."""
        # ⚠ Signature exacte de `.transcribe()` pour canary-1b-v2 (source_lang/target_lang/
        # task/pnc en kwargs directs vs objet de config) non confirmée par exécution — d'après
        # la doc HuggingFace du modèle, `task="ast"` déclenche la traduction (vs "asr" pour la
        # transcription same-langue).
        hypotheses = self._model.transcribe(
            audio=[audio],
            source_lang="en",
            target_lang=target_lang,
            task="ast",
            pnc="yes",
        )
        return hypotheses[0].text if hasattr(hypotheses[0], "text") else str(hypotheses[0])
