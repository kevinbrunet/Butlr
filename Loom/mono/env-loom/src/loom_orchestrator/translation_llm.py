from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from llama_cpp import Llama as LlamaType

# ⚠ Modèle/quantization non calibré empiriquement (ADR-0043, 2026-07) — Qwen3-4B-Instruct-2507
# choisi pour son écosystème GGUF mature (quantifications officielles ggml-org + bartowski,
# déjà largement utilisées, donc risque de compatibilité llama.cpp plus faible) plutôt que
# Qwen3.5-4B (plus récent au moment de l'écriture, moins de recul communautaire). Q8_0 choisi
# plutôt qu'une quantization plus agressive (Q4_K_M/Q5_K_M) : le budget VRAM restant (~18 Go
# une fois WLK/Sortformer/SepFormer/ECAPA/Pocket TTS chargés, cf. ADR-0042) est large pour un
# modèle 4B — la qualité de traduction prime sur l'empreinte mémoire ici. À réévaluer si la
# qualité s'avère insuffisante (cf. harness_llm_translate.py).
DEFAULT_MODEL_REPO = "bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF"
DEFAULT_MODEL_FILE = "Qwen_Qwen3-4B-Instruct-2507-Q8_0.gguf"

# ✓ Codes ISO 639-1, cohérents avec resolve_language_code de translation_seamless.py — mais
# ce module attend directement des noms de langue en anglais dans le prompt (pas de code
# Seamless ISO 639-3, le petit Qwen n'a pas besoin de ce format).
LANGUAGE_NAMES = {
    "en": "English",
    "zh": "Chinese",
    "fr": "French",
}

MAX_TRANSLATION_TOKENS = 512


def build_messages(text: str, source_lang: str, target_lang: str) -> list[dict]:
    src_name = LANGUAGE_NAMES.get(source_lang, source_lang)
    tgt_name = LANGUAGE_NAMES.get(target_lang, target_lang)
    return [
        {
            "role": "system",
            "content": (
                f"You are a professional simultaneous interpreter. Translate the "
                f"following {src_name} text into {tgt_name}. Output only the "
                "translation itself — no commentary, no explanations, no quotation "
                "marks around the result."
            ),
        },
        {"role": "user", "content": text},
    ]


class LlmTranslator:
    """Traduction via un petit modèle Qwen embarqué (llama.cpp, bindings `llama-cpp-python`,
    ADR-0043) — remplace `SeamlessTranslator`/`AlignAttSeamlessTranslator`
    (`translation_seamless.py`, ADR-0040/0041 supersedées sur la traduction).

    Attention causale (decoder-only) : contrairement à l'encodeur ~bidirectionnel de Seamless
    (cf. ADR-0043 §Context), étendre le contexte est un ajout strict — pas de recalcul de
    l'existant, pas de mismatch encodeur/décodeur entre appels. Ce module ne l'exploite pas
    encore (chaque appel à `translate` est indépendant, pas de KV-cache réutilisé entre
    appels) — la politique de commit incrémental (cf. ADR-0043 §Conséquences) reste à
    trancher avant d'en tirer parti.

    ⚠ Chargement par repo HuggingFace (`Llama.from_pretrained` télécharge et met en cache le
    GGUF via `huggingface_hub`) et `n_gpu_layers=-1` (offload complet sur GPU) non vérifiés
    par exécution réelle sur la machine cible.
    """

    def __init__(
        self,
        model_repo: str = DEFAULT_MODEL_REPO,
        model_file: str = DEFAULT_MODEL_FILE,
        n_gpu_layers: int = -1,
        n_ctx: int = 4096,
    ) -> None:
        from llama_cpp import Llama

        self._model: LlamaType = Llama.from_pretrained(
            repo_id=model_repo,
            filename=model_file,
            n_gpu_layers=n_gpu_layers,
            n_ctx=n_ctx,
            verbose=False,
        )

    def translate(self, text: str, source_lang: str, target_lang: str = "fr") -> str:
        """Traduit `text` de `source_lang` vers `target_lang` — traduction complète d'un
        segment (pas de commit incrémental token-par-token à ce stade, cf. docstring de
        classe)."""
        if not text.strip():
            return ""
        response = self._model.create_chat_completion(
            messages=build_messages(text, source_lang, target_lang),
            temperature=0.0,
            max_tokens=MAX_TRANSLATION_TOKENS,
        )
        return response["choices"][0]["message"]["content"].strip()
