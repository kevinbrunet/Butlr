from __future__ import annotations

import re
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from llama_cpp import Llama as LlamaType

# Qwen3.5-9B Q8_0 (2026-10) : remplace Qwen3-4B-Instruct-2507 Q8_0 (ADR-0043) après comparatif
# sur le texte source exact des corpus a et b, découpé comme le pipeline : chrF 67,3 / 71,4 contre
# 61,9 / 65,8 pour Qwen3-4B, p50 ~90-120 ms par fragment, ~10 Go de VRAM. Hunyuan-MT-7B et
# TranslateGemma-12B écartés : avec contexte, ils retraduisent des fragments déjà émis.
# ✓ Confirmé dans le pipeline (2026-10-28, 3 runs par variante, chrF de la traduction produite) :
# mono 60,5 → 66,3 (Voxtral) et 61,6 → 65,0 (WLK), délai p50 +35 ms (cf. Révisions ADR-0043).
DEFAULT_MODEL_REPO = "unsloth/Qwen3.5-9B-GGUF"
DEFAULT_MODEL_FILE = "Qwen3.5-9B-Q8_0.gguf"

# Variables passées au gabarit de chat embarqué dans le GGUF : Qwen3.5 raisonne par défaut
# (bloc <think>), ce qui ajoute des centaines de jetons de latence par fragment.
CHAT_TEMPLATE_KWARGS = {"enable_thinking": False}

# ✓ Codes ISO 639-1, cohérents avec resolve_language_code de translation_seamless.py — mais
# ce module attend directement des noms de langue en anglais dans le prompt (pas de code
# Seamless ISO 639-3, le petit Qwen n'a pas besoin de ce format).
LANGUAGE_NAMES = {
    "en": "English",
    "zh": "Chinese",
    "fr": "French",
}

MAX_TRANSLATION_TOKENS = 512

# Mode « contexte continu » (`keep_context=True`) : chaque segment est traduit à la suite des
# échanges précédents du même locuteur (source, traduction), dans une conversation qui ne fait
# que s'allonger. Le préfixe envoyé à llama.cpp est donc identique d'un appel à l'autre, et
# `llama-cpp-python` réutilise son cache KV sur ce préfixe : seul le nouveau segment est
# calculé. ⚠ Pas encore de compactage : quand la conversation dépasse la fenêtre, les échanges
# les plus anciens sont retirés, ce qui invalide le cache une fois (recalcul complet).
CONTEXT_N_CTX = 32768
CONTEXT_MARGIN_TOKENS = MAX_TRANSLATION_TOKENS + 256


def build_messages(
    text: str,
    source_lang: str,
    target_lang: str,
    history: "list[tuple[str, str]] | None" = None,
) -> list[dict]:
    """`history` = None : segment traduit seul (comportement d'origine). Une liste, même
    vide : mode contexte continu, échanges précédents rejoués en tours user/assistant."""
    src_name = LANGUAGE_NAMES.get(source_lang, source_lang)
    tgt_name = LANGUAGE_NAMES.get(target_lang, target_lang)
    system = (
        f"You are a professional simultaneous interpreter. Translate the "
        f"following {src_name} text into {tgt_name}. Output only the "
        "translation itself — no commentary, no explanations, no quotation "
        "marks around the result."
    )
    if history is None:
        return [{"role": "system", "content": system}, {"role": "user", "content": text}]
    system += (
        " The text arrives in successive fragments of one continuous speech, often cut "
        "mid-sentence. Translate only the latest fragment, so that it continues your previous "
        "translations naturally; do not repeat or revise them."
    )
    messages = [{"role": "system", "content": system}]
    for source, translation in history:
        messages.append({"role": "user", "content": source})
        messages.append({"role": "assistant", "content": translation})
    messages.append({"role": "user", "content": text})
    return messages


class LlmTranslator:
    """Traduction via un petit modèle Qwen embarqué (llama.cpp, bindings `llama-cpp-python`,
    ADR-0043) — remplace `SeamlessTranslator`/`AlignAttSeamlessTranslator`
    (`translation_seamless.py`, ADR-0040/0041 supersedées sur la traduction).

    Attention causale (decoder-only) : contrairement à l'encodeur ~bidirectionnel de Seamless
    (cf. ADR-0043 §Context), étendre le contexte est un ajout strict — pas de recalcul de
    l'existant, pas de mismatch encodeur/décodeur entre appels. Exploité par
    `keep_context=True` (conversation continue, cache KV réutilisé sur le préfixe) ; par
    défaut, chaque appel à `translate` reste indépendant.

    ⚠ Chargement par repo HuggingFace (`Llama.from_pretrained` télécharge et met en cache le
    GGUF via `huggingface_hub`) et `n_gpu_layers=-1` (offload complet sur GPU) non vérifiés
    par exécution réelle sur la machine cible.
    """

    def __init__(
        self,
        model_repo: str = DEFAULT_MODEL_REPO,
        model_file: str = DEFAULT_MODEL_FILE,
        n_gpu_layers: int = -1,
        n_ctx: int | None = None,
        keep_context: bool = False,
    ) -> None:
        from llama_cpp import Llama

        self._keep_context = keep_context
        self._n_ctx = n_ctx or (CONTEXT_N_CTX if keep_context else 4096)
        self._histories: dict[object, list[tuple[str, str]]] = {}
        self._model: LlamaType = Llama.from_pretrained(
            repo_id=model_repo,
            filename=model_file,
            n_gpu_layers=n_gpu_layers,
            n_ctx=self._n_ctx,
            verbose=False,
        )
        self._render = _chat_template_renderer(self._model)

    def translate(
        self, text: str, source_lang: str, target_lang: str = "fr", context_key: object = 0
    ) -> str:
        """Traduit `text` de `source_lang` vers `target_lang`. En mode contexte continu,
        `context_key` sépare les historiques (un par locuteur en multi) ; passer d'une clé à
        l'autre change le préfixe, donc le cache KV est recalculé pour la nouvelle clé."""
        if not text.strip():
            return ""
        history = self._histories.setdefault(context_key, []) if self._keep_context else None
        messages = build_messages(text, source_lang, target_lang, history)
        if history is not None:
            while history and self._count_tokens(messages) > self._n_ctx - CONTEXT_MARGIN_TOKENS:
                history.pop(0)
                messages = build_messages(text, source_lang, target_lang, history)
        # Prompt rendu ici plutôt que par `create_chat_completion`, qui ne transmet pas de
        # variables au gabarit (`enable_thinking`). Le rendu reste identique d'un appel à
        # l'autre sur l'historique, donc le cache KV est toujours réutilisé sur le préfixe.
        response = self._model.create_completion(
            self._render(messages),
            temperature=0.0,
            max_tokens=MAX_TRANSLATION_TOKENS,
        )
        translated = strip_thinking(response["choices"][0]["text"])
        if history is not None:
            history.append((text, translated))
        return translated

    def _count_tokens(self, messages: list[dict]) -> int:
        # Approximation : contenu tokenisé + ~8 jetons de balisage de tour par message.
        return sum(
            len(self._model.tokenize(m["content"].encode("utf-8"), add_bos=False)) + 8
            for m in messages
        )


def strip_thinking(text: str) -> str:
    """Retire un éventuel bloc de raisonnement `<think>…</think>` (vide quand
    `enable_thinking=False`, mais certains gabarits l'émettent quand même)."""
    return re.sub(r"<think>.*?</think>", "", text, flags=re.S).strip()


def _chat_template_renderer(model: "LlamaType"):
    import jinja2
    from jinja2.sandbox import ImmutableSandboxedEnvironment

    def raise_exception(message: str):
        raise jinja2.TemplateError(message)

    env = ImmutableSandboxedEnvironment()
    env.globals["raise_exception"] = raise_exception
    template = env.from_string(model.metadata["tokenizer.chat_template"])
    bos_id, eos_id = model.token_bos(), model.token_eos()
    bos = model.detokenize([bos_id]).decode("utf-8", "replace") if bos_id >= 0 else ""
    eos = model.detokenize([eos_id]).decode("utf-8", "replace") if eos_id >= 0 else ""

    def render(messages: list[dict]) -> str:
        return template.render(
            messages=messages,
            add_generation_prompt=True,
            bos_token=bos,
            eos_token=eos,
            **CHAT_TEMPLATE_KWARGS,
        )

    return render
