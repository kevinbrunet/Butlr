# scripts — setup Phase 1 (PowerShell)

Outillage pour monter la stack locale de Carlson : llama.cpp server + Qwen 2.5 7B GGUF, faster-whisper, Piper TTS.

## Prérequis

- Windows 10/11
- GPU NVIDIA, drivers récents
- CUDA Toolkit installé (`nvcc` sur PATH)
- Visual Studio 2022 avec *Desktop development with C++*
- CMake ≥ 3.24, Git, Python 3.11+, `curl.exe` (fourni par Windows depuis 1803)
- **Exécuter ces scripts depuis *Developer PowerShell for VS 2022*** (pour que `cl.exe` soit disponible pour le build CUDA de llama.cpp).

## Configuration

```powershell
cd scripts
Copy-Item .\env.example.ps1 .\env.ps1
# Édite env.ps1 si tu veux changer BUTLR_ENV_DIR, les ports, la voix, etc.
```

Par défaut, tout atterrit dans `%USERPROFILE%\butlr-env\` (source llama.cpp, modèles, voix) — hors du repo pour éviter les gigas en git.

## Pipeline

À lancer dans l'ordre, depuis un Developer PowerShell for VS 2022 positionné dans `scripts\` :

```powershell
# 1. Sanity check : GPU, CUDA, toolchain, Python, curl.
.\Check-Prereqs.ps1

# 2. Clone + build llama.cpp avec CUDA (~5-15 min selon la machine).
.\Build-Llama.ps1

# 3. Download du GGUF Qwen 2.5 7B Q5_K_M (~5,4 GB).
.\Get-LlamaModel.ps1

# 4. Lance llama-server en foreground (Ctrl+C pour arrêter).
.\Start-LlamaServer.ps1
```

Dans un **second terminal** (llama-server tourne dans le premier) :

```powershell
# 5. Smoke test HTTP OpenAI-compat.
.\Test-LlamaServer.ps1
```

### Whisper (STT)

```powershell
python -m venv .venv-whisper
.\.venv-whisper\Scripts\Activate.ps1
pip install faster-whisper
# Lance sur un WAV de test (16 kHz mono idéalement).
python .\Test-Whisper.py .\chemin\vers\sample.wav
```

### Piper (TTS)

```powershell
python -m venv .venv-piper
.\.venv-piper\Scripts\Activate.ps1
pip install piper-tts

.\Get-PiperVoices.ps1     # download FR + EN
.\Test-Piper.ps1          # synthèse de samples
```

## Conventions

- Nommage PowerShell Verb-Noun (`Build-Llama.ps1`, `Get-LlamaModel.ps1`, etc.).
- Tous les scripts dot-source `_Lib.ps1` puis appellent `Import-BtlrEnv` pour charger `env.ps1` (ou `env.example.ps1` en fallback).
- `curl.exe` explicite (pas l'alias PS de `Invoke-WebRequest`) pour les gros downloads.
- `Set-StrictMode -Version Latest` + `$ErrorActionPreference = 'Stop'` partout.
- Pour les exit codes natifs non-zéro, check explicite de `$LASTEXITCODE`.

## Notes

- Les versions (llama.cpp HEAD, GGUF, faster-whisper, piper-tts) ne sont **pas épinglées** ici — on valide la stack avant de pinner. À faire avant Phase 2.
- `--jinja` (`env.example.ps1`) est nécessaire ~ pour que llama.cpp formatte correctement le tool calling OpenAI-compat. À confirmer avec la version installée au moment du build.
- Aucune TLS : LAN de dev. Le bearer token entre Carlson et mcp-home est défini dans l'environnement de Carlson, pas ici.
