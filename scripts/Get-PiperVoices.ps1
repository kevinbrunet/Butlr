# Butlr / Phase 1 — download des voix Piper FR + EN.
#
# Chaque voix = 2 fichiers :
#   - <name>.onnx       (modèle)
#   - <name>.onnx.json  (config / speakers / sample rate)
#
# Repo des voix : huggingface.co/rhasspy/piper-voices ✓
# La structure du repo est /<lang_code_2>/<locale>/<speaker>/<quality>/

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Assert-Cmd -Name "curl.exe"

Test-PathExists -Path $env:VOICES_DIR -CreateIfMissing | Out-Null

function Get-PiperVoice {
    param(
        [Parameter(Mandatory)][string]$Name,   # ex. fr_FR-siwis-medium
        [Parameter(Mandatory)][string]$SubPath # ex. fr/fr_FR/siwis/medium
    )
    $base = "$env:PIPER_VOICES_BASE_URL/$SubPath"
    $destDir = Join-Path $env:VOICES_DIR $Name
    Test-PathExists -Path $destDir -CreateIfMissing | Out-Null

    foreach ($ext in @("onnx", "onnx.json")) {
        $fileName = "$Name.$ext"
        $url      = "$base/$fileName"
        $dest     = Join-Path $destDir $fileName

        if (Test-Path -LiteralPath $dest) {
            Write-Warn2 "Déjà présent : $dest"
            continue
        }

        Write-Info "Download : $url"
        curl.exe -L -# --fail -o $dest $url
        if ($LASTEXITCODE -ne 0) {
            throw "Download échoué pour $fileName (curl exit $LASTEXITCODE)."
        }
        Write-Ok "OK : $dest"
    }
}

Get-PiperVoice -Name $env:PIPER_VOICE_FR_NAME -SubPath $env:PIPER_VOICE_FR_PATH
Get-PiperVoice -Name $env:PIPER_VOICE_EN_NAME -SubPath $env:PIPER_VOICE_EN_PATH

Write-Ok "Voix Piper téléchargées dans : $env:VOICES_DIR"
