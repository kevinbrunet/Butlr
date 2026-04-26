"""Runtime configuration — read from environment variables.

Keep this file the single place that reads os.environ. Everything else takes
a Config instance.

Défauts alignés avec les ADR du 2026-04-23 :
- LLM : llama.cpp server (port 8080 par défaut ~), cf. ADR 0006.
- MCP : mcp-home en service long-running exposé en SSE/HTTP sur le port 5090 ~,
  cf. ADR 0003. Plus de spawn en sous-processus.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _default_stt_model() -> str:
    # Pointe sur le chemin local téléchargé par Get-WhisperModel.ps1.
    # Si BUTLR_ENV_DIR n'est pas défini, on recalcule le même défaut que env.example.ps1.
    butlr_env = os.getenv("BUTLR_ENV_DIR", str(Path.home() / "butlr-env"))
    return str(Path(butlr_env) / "models" / "whisper" / "faster-whisper-large-v3")


def _default_wakeword_model() -> str:
    # hey_carlson.onnx si présent (modèle entraîné localement).
    # Sinon, repli sur hey_jarvis pré-entraîné OWW (validé, fonctionne out-of-the-box).
    import openwakeword.utils
    carlson_root = Path(__file__).parent.parent.parent
    custom = carlson_root / "assets" / "wakeword" / "hey_carlson.onnx"
    if custom.exists():
        return str(custom)
    oww_resources = Path(openwakeword.utils.__file__).parent / "resources" / "models"
    jarvis = oww_resources / "hey_jarvis_v0.1.onnx"
    return str(jarvis) if jarvis.exists() else str(oww_resources / "hey_jarvis_v0.1.tflite")


@dataclass(frozen=True)
class Config:
    # LLM (llama.cpp server, OpenAI-compatible)
    llm_base_url: str
    llm_model: str
    llm_api_key: str  # llama.cpp ignores it but the OpenAI SDK requires a value

    # STT — nom HF ("large-v3") ou chemin absolu vers un dossier CTranslate2 local
    stt_model: str

    # TTS
    tts_engine: str  # "piper" | "xtts"
    tts_voice_fr: str
    tts_voice_en: str

    # Wake word
    wakeword_model: str
    wakeword_threshold: float

    # MCP — SSE/HTTP (cf. ADR 0003)
    mcp_home_url: str        # ex. http://localhost:5090/mcp
    mcp_home_token: str      # bearer token partagé ; vide = pas d'auth (dev local uniquement)

    # Filler
    filler_delay_ms: int

    # Misc
    language_default: str  # "fr" | "en"
    use_vad: bool      # True = Silero VAD auto-detect; False = push-to-talk (Enter key)
    use_wakeword: bool  # True = "Hey Carlson" requis avant tout tour; implique use_vad=True

    @classmethod
    def from_env(cls) -> "Config":
        return cls(
            llm_base_url=os.getenv("LLM_BASE_URL", "http://localhost:8080/v1"),
            llm_model=os.getenv("LLM_MODEL", "Qwen/Qwen2.5-7B-Instruct"),
            llm_api_key=os.getenv("LLM_API_KEY", "local-not-used"),
            stt_model=os.getenv("STT_MODEL", _default_stt_model()),
            tts_engine=os.getenv("TTS_ENGINE", "piper"),
            tts_voice_fr=os.getenv("TTS_VOICE_FR", "fr_FR-gilles-low"),
            tts_voice_en=os.getenv("TTS_VOICE_EN", "en_GB-alan-medium"),
            wakeword_model=os.getenv("WAKEWORD_MODEL", _default_wakeword_model()),
            wakeword_threshold=float(os.getenv("WAKEWORD_THRESHOLD", "0.5")),
            mcp_home_url=os.getenv("MCP_HOME_URL", "https://localhost:5001/mcp"),
            mcp_home_token=os.getenv("MCP_HOME_TOKEN", ""),
            filler_delay_ms=int(os.getenv("FILLER_DELAY_MS", "500")),
            language_default=os.getenv("LANGUAGE_DEFAULT", "fr"),
            use_vad=os.getenv("USE_VAD", "1").lower() in ("1", "true", "yes"),
            use_wakeword=os.getenv("USE_WAKEWORD", "1").lower() in ("1", "true", "yes"),
        )
