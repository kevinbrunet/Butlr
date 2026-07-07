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
