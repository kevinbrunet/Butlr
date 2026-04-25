# Butlr / Phase 4 — Prépare l'entraînement du wake word "Hey Carlson".
#
# ⚠ L'entraînement openWakeWord ne fonctionne que sous Linux.
#   Ce script affiche les instructions Colab ou génère la config YAML.
#   Pour l'entraînement en local, utilise WSL2 (voir instructions affichées).
#
# Usage :
#   .\Train-WakeWord.ps1                  # affiche le guide Colab
#   .\Train-WakeWord.ps1 -GenerateConfig  # écrit le YAML de config uniquement

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

param(
    [switch] $GenerateConfig
)

$repoRoot    = Split-Path $PSScriptRoot -Parent
$carlsonDir  = Join-Path $repoRoot 'carlson'
$venvPython  = Join-Path $carlsonDir '.venv\Scripts\python.exe'
$trainScript = Join-Path $carlsonDir 'scripts\train_wakeword.py'

# -- Vérifie le venv -----------------------------------------------------------
if (-not (Test-Path -LiteralPath $venvPython)) {
    Write-Error (
        "Python du venv carlson introuvable : $venvPython`n" +
        "Crée-le d'abord : cd carlson && python -m venv .venv && .venv\Scripts\Activate.ps1 && pip install -e '.[all,dev]'"
    )
}

if ($GenerateConfig) {
    Write-Host "Génération du fichier de config YAML…" -ForegroundColor Cyan
    & $venvPython $trainScript --generate-config
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Échec de la génération de config (exit $LASTEXITCODE)."
    }
} else {
    # Affiche le guide complet (Colab + WSL2)
    & $venvPython $trainScript
}
