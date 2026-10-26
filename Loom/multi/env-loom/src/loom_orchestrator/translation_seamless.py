from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    import numpy as np

SAMPLE_RATE_HZ = 16_000
MODEL_NAME = "facebook/seamless-m4t-v2-large"

# ✓ Codes ISO 639-3 vérifiés dans facebook/seamless-m4t-v2-large/README.md (lu le
# 2026-07-14) : eng=anglais, cmn=mandarin simplifié, fra=français.
LANGUAGE_CODES = {
    "en": "eng",
    "zh": "cmn",
    "fr": "fra",
}


def resolve_language_code(code: str) -> str:
    """Convertit un code ISO 639-1 (en/zh/fr) en code Seamless (eng/cmn/fra).

    Un code déjà au format Seamless (3 lettres, ex. "eng") passe inchangé.
    """
    return LANGUAGE_CODES.get(code, code)


class SeamlessTranslator:
    """Traduction parole-vers-texte (pas de vocoder) via SeamlessM4T v2 — Phase 1 de
    ADR-0040 : remplace NLLB, un tour de parole complet à la fois (pas de streaming
    mot-à-mot). La sortie texte est destinée à Pocket TTS, jamais au module Expressive
    de Seamless.

    ✓ Vérifié (doc HuggingFace `transformers`, lue le 2026-07-14) :
    `SeamlessM4Tv2ForSpeechToText` ne charge que l'encodeur parole + décodeur texte
    (~5,8 Go VRAM en fp16) — pas le modèle text-to-unit ni le vocoder. L'encodeur parole
    gère nativement l'entrée multilingue : `tgt_lang` seul suffit, pas besoin de langue
    source explicite comme pour NLLB (texte).

    ⚠ Constaté empiriquement (premier run réel, 2026-07-15, corpus ZH, segments de 10s) :
    plus aucune hallucination de mot type "auto"/"voiture" (contrairement à NLLB, cf.
    ADR-0040) — mais boucles de répétition récurrentes en fin de segment ("les maisons,
    les maisons, les maisons..." ×20). Mode de dégénérescence connu du décodage
    autoregressif glouton sur des séquences longues, pas la même cause que le bug NLLB.
    `no_repeat_ngram_size`/`repetition_penalty` ajoutés à `generate()` pour le corriger —
    valeurs non calibrées empiriquement, à ajuster selon le prochain run.
    """

    def __init__(self, model_name: str = MODEL_NAME, device: str = "cuda") -> None:
        from transformers import AutoProcessor, SeamlessM4Tv2ForSpeechToText

        self._processor = AutoProcessor.from_pretrained(model_name)
        self._model = SeamlessM4Tv2ForSpeechToText.from_pretrained(model_name).to(device)
        self._device = device

    def translate(self, audio: np.ndarray, target_lang: str = "fr") -> str:
        """Traduit un segment audio 16kHz mono (un tour de parole complet) vers
        `target_lang` (code ISO 639-1, ex. "fr")."""
        tgt_lang = resolve_language_code(target_lang)
        # ✓ "audio=" (pas "audios=", déprécié depuis transformers 4.59 — corrigé après le
        # FutureWarning observé au premier run réel).
        inputs = self._processor(audio=audio, sampling_rate=SAMPLE_RATE_HZ, return_tensors="pt")
        inputs = {k: v.to(self._device) for k, v in inputs.items()}
        output_tokens = self._model.generate(
            **inputs,
            tgt_lang=tgt_lang,
            no_repeat_ngram_size=3,
            repetition_penalty=1.2,
        )[0]
        return self._processor.decode(output_tokens.tolist(), skip_special_tokens=True)


# ⚠ Valeur de départ non calibrée empiriquement (ADR-0041) — nombre de frames encodeur, pas
# une durée, pour ne pas dépendre du taux de trame réel de l'encodeur parole de SeamlessM4T v2
# (non vérifié). À ajuster une fois mesuré sur la machine cible.
FRONTIER_FRAMES_DEFAULT = 4


class AlignAttSeamlessTranslator:
    """Traduction incrémentale via AlignAtt (Papi et al., Interspeech 2023) appliqué à
    l'attention croisée de `SeamlessM4Tv2ForSpeechToText` — politique de commit de
    ADR-0041, remplace l'attente d'un tour de parole complet par une décision continue
    "ce préfixe de traduction est-il sûr à émettre étant donné l'audio disponible".

    ⚠ Chaque appel à `translate_partial` ré-encode l'intégralité de l'audio fourni depuis
    zéro (pas de cache décodeur réutilisé entre appels à audio de longueur différente) —
    reproduire ce pattern de cache serait reproduire le bug `_continue_generation_with_cache`
    qui a cassé NLLB (cf. ADR-0040). Le coût CPU/GPU grandit donc avec la longueur de la ligne
    en cours — pas encore mesuré (à faire une fois câblé dans `harness_pipeline.py`).

    ⚠ Forme exacte de `outputs.cross_attentions` (transformers `generate(output_attentions=
    True, return_dict_in_generate=True)`) non vérifiée par exécution réelle — des
    incohérences de forme entre versions/modèles sont documentées côté transformers (issues
    GitHub #11788, #17327, #33296). Le code ci-dessous prend systématiquement la **dernière**
    ligne de la dimension "longueur générée" de chaque tenseur d'attention, ce qui doit
    correspondre au token courant que la forme soit `(..., 1, src_len)` (un pas à la fois) ou
    `(..., gen_len_so_far, src_len)` (cumulatif) — à confirmer sur le premier run réel.
    """

    def __init__(
        self,
        model_name: str = MODEL_NAME,
        device: str = "cuda",
        frontier_frames: int = FRONTIER_FRAMES_DEFAULT,
    ) -> None:
        from transformers import AutoProcessor, SeamlessM4Tv2ForSpeechToText

        self._processor = AutoProcessor.from_pretrained(model_name)
        self._model = SeamlessM4Tv2ForSpeechToText.from_pretrained(model_name).to(device)
        self._device = device
        self._frontier_frames = frontier_frames

    def translate_partial(self, audio: np.ndarray, target_lang: str = "fr") -> str:
        """Traduit l'audio disponible jusqu'ici et retourne uniquement le préfixe "sûr"
        (cf. `alignatt.safe_token_count`) — pas le texte complet généré. L'appelant est
        responsable de diffuser ce préfixe contre le texte déjà commité (cf.
        `alignatt.compute_increment`) pour n'envoyer au TTS que l'increment nouveau.
        """
        from loom_orchestrator.alignatt import safe_token_count

        tgt_lang = resolve_language_code(target_lang)
        inputs = self._processor(audio=audio, sampling_rate=SAMPLE_RATE_HZ, return_tensors="pt")
        inputs = {k: v.to(self._device) for k, v in inputs.items()}

        outputs = self._model.generate(
            **inputs,
            tgt_lang=tgt_lang,
            no_repeat_ngram_size=3,
            repetition_penalty=1.2,
            output_attentions=True,
            return_dict_in_generate=True,
        )

        token_ids = outputs.sequences[0]
        cross_attentions = outputs.cross_attentions
        if not cross_attentions:
            return ""

        encoder_seq_len = cross_attentions[0][-1].shape[-1]
        attended_frames = []
        for step_attn in cross_attentions:
            last_layer_attn = step_attn[-1]  # (batch, heads, gen_len_so_far, src_len)
            avg_over_heads = last_layer_attn[0].mean(dim=0)  # (gen_len_so_far, src_len)
            attended_frames.append(int(avg_over_heads[-1].argmax().item()))

        n_safe = safe_token_count(attended_frames, encoder_seq_len, self._frontier_frames)
        if n_safe == 0:
            return ""

        # token_ids[0] est le token de début de séquence (BOS/langue cible), pas un token
        # généré — cf. convention seq2seq de transformers.
        safe_tokens = token_ids[1 : 1 + n_safe]
        return self._processor.decode(safe_tokens.tolist(), skip_special_tokens=True)
