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

# Paramètres de la politique de décodage streaming native NeMo (`AEDStreamingDecodingConfig`).
# ✓ Noms de champs et valeurs par défaut confirmés le 2026-08-15 par introspection directe de
# la dataclass sur la machine cible (`dataclasses.fields(AEDStreamingDecodingConfig)`) — PAS la
# doc NeMo, qui s'est révélée fausse sur ce point précis (cf. ADR-0047 Révisions 2026-08-15) :
# `policy=` n'existe pas (c'est `streaming_policy=`), et `chunk_secs`/`left_context_secs`/
# `right_context_secs` (censés borner le coût par étape, argument central de la première version
# de l'ADR-0047) **n'existent pas du tout** sur cette classe — la doc décrivait probablement une
# fonctionnalité différente (chunking pour l'inférence longue, `chunk_len_in_secs`, pas la
# politique de streaming elle-même). Aucune garantie de contexte borné n'est donc confirmée pour
# l'instant — à vérifier empiriquement (coût par appel en fonction de la durée d'audio fournie),
# pas à supposer comme pour Seamless (ADR-0041/0043).
ALIGNATT_THR_DEFAULT = 8
XATT_SCORES_LAYER_DEFAULT = -2


class AlignAttCanaryTranslator:
    """Traduction speech-to-text EN→FR via `nvidia/canary-1b-v2` (NeMo), politique de
    décodage streaming AlignAtt **native** à NeMo (`AEDStreamingDecodingConfig`,
    `streaming_policy="alignatt"`) — cf. ADR-0047.

    Contrairement à `AlignAttSeamlessTranslator` (ADR-0041), qui ré-implémente AlignAtt à la
    main contre les tenseurs d'attention bruts de `transformers` (parce qu'aucune politique de
    streaming n'existait pour Seamless), ici NeMo porte la logique de frontière directement :
    ce module ne fait qu'appeler l'API NeMo documentée avec les bons paramètres, pas de calcul
    de frontière ici (`loom_orchestrator/alignatt.py` n'est pas utilisé sur ce chemin).

    ✓ Chargement du modèle et construction de `AEDStreamingDecodingConfig` exécutés avec succès
    sur la machine cible le 2026-08-15 (noms de champs corrigés par introspection directe, cf.
    ADR-0047 Révisions — la doc NeMo s'est révélée fausse sur `policy=` et sur l'existence même
    de `chunk_secs`/`left_context_secs`/`right_context_secs`, retirés). ⚠ `translate()` reste
    non vérifié par exécution — signature exacte de `.transcribe()` (kwargs `source_lang`/
    `target_lang`/`task`/`pnc`, format de `hypotheses[0]`) toujours écrite contre la doc
    HuggingFace du modèle uniquement.

    ⚠ Bug connu non résolu au 2026-08-15 (`NVIDIA-NeMo/NeMo#15231`) : le décodage streaming
    AlignAtt sur `canary-1b-v2` se bloquerait après ~20-40s sur de l'audio continu long. Premier
    test à faire avant toute autre validation (cf. ADR-0047 §Consequences) — pas contourné ici.
    """

    def __init__(
        self,
        model_name: str = MODEL_NAME,
        alignatt_thr: int = ALIGNATT_THR_DEFAULT,
        xatt_scores_layer: int = XATT_SCORES_LAYER_DEFAULT,
    ) -> None:
        # ⚠ Import différé (comme les autres traducteurs) : `nemo_toolkit['asr']` chargé depuis
        # `env-loom` (déjà présent avant ADR-0047 pour Sortformer, cf. `pyproject.toml`).
        from nemo.collections.asr.models import EncDecMultiTaskModel
        from nemo.collections.asr.parts.submodules.multitask_decoding import (
            AEDStreamingDecodingConfig,
        )

        self._model = EncDecMultiTaskModel.from_pretrained(model_name)
        self._streaming_cfg = AEDStreamingDecodingConfig(
            streaming_policy="alignatt",
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
