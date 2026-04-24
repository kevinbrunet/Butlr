# Butlr / Phase 1 — télécharge le modèle faster-whisper large-v3 dans butlr-env.
#
# Utilise huggingface_hub (inclus avec faster-whisper) depuis le venv carlson.
# ~ Taille attendue : ~3 GB pour Systran/faster-whisper-large-v3.
# Le modèle est téléchargé une seule fois ; si le dossier cible existe déjà,
# le script sort sans rien faire.

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

$dest = $env:WHISPER_MODEL_DIR
if (-not $dest) {
    throw "Variable WHISPER_MODEL_DIR non définie. Vérifie env.ps1 / env.example.ps1."
}

# Présence vérifiée sur model.bin (fichier principal CTranslate2).
$modelBin = Join-Path $dest "model.bin"
if (Test-Path -LiteralPath $modelBin) {
    $sizeMB = [math]::Round((Get-Item -LiteralPath $modelBin).Length / 1MB, 0)
    Write-Ok "Modèle Whisper déjà présent : $dest (model.bin = $sizeMB MB)"
    Write-Host "     Supprime manuellement le dossier pour re-télécharger." -ForegroundColor DarkGray
    return
}

Test-PathExists -Path $dest -CreateIfMissing | Out-Null

# Python du venv carlson — huggingface_hub y est inclus via faster-whisper.
$venvPython = Join-Path $PSScriptRoot "..\carlson\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $venvPython)) {
    throw @"
Venv carlson introuvable : $venvPython
Lance d'abord depuis le dossier carlson/ :
    python -m venv .venv
    .venv\Scripts\Activate.ps1
    pip install -e .[all]
"@
}

Write-Info "Téléchargement Systran/faster-whisper-large-v3 vers :"
Write-Info "  $dest"
Write-Info "(peut prendre plusieurs minutes selon la connexion)"

# Script Python dans un fichier temporaire pour éviter les problèmes de quoting.
$tmpScript = [System.IO.Path]::GetTempFileName() + ".py"
$destEscaped = $dest -replace '\\', '\\'
@"
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id="Systran/faster-whisper-large-v3",
    local_dir=r"$destEscaped",
)
print("done")
"@ | Set-Content -Path $tmpScript -Encoding utf8

try {
    Invoke-NativeCommand $venvPython $tmpScript
} finally {
    Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
}

Write-Ok "Modèle Whisper disponible : $dest"
