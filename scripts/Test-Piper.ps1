# Butlr / Phase 1 — smoke test Piper TTS sur FR + EN.
#
# Piper s'installe via pip (`pip install piper-tts`) dans un venv Python.
# Ce script suppose que `piper` est sur le PATH (donc le venv est activé).
#
# Usage :
#   python -m venv .venv
#   .\.venv\Scripts\Activate.ps1
#   pip install piper-tts
#   .\Get-PiperVoices.ps1
#   .\Test-Piper.ps1
#
# Référence CLI Piper : https://github.com/rhasspy/piper ~

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Assert-Cmd -Name "piper" -Hint "Dans un venv activé : pip install piper-tts"

function Invoke-PiperSay {
    param(
        [Parameter(Mandatory)][string]$VoiceName, # ex. fr_FR-siwis-medium
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$OutWav
    )
    $modelPath = Join-Path $env:VOICES_DIR "$VoiceName\$VoiceName.onnx"
    if (-not (Test-Path -LiteralPath $modelPath)) {
        throw "Modèle voix introuvable : $modelPath (lance Get-PiperVoices.ps1)"
    }

    Write-Info "Synthèse '$VoiceName' -> $OutWav"
    # Piper lit le texte sur stdin, écrit le WAV sur --output_file.
    $Text | piper --model $modelPath --output_file $OutWav
    if ($LASTEXITCODE -ne 0) {
        throw "Piper a échoué (exit $LASTEXITCODE) pour $VoiceName."
    }

    if (-not (Test-Path -LiteralPath $OutWav)) {
        throw "WAV non généré : $OutWav"
    }

    $sizeKB = [math]::Round((Get-Item -LiteralPath $OutWav).Length / 1KB, 1)
    Write-Ok "OK : $OutWav ($sizeKB KB)"
}

$outDir = Join-Path $env:BUTLR_ENV_DIR "piper-samples"
Test-PathExists -Path $outDir -CreateIfMissing | Out-Null

Invoke-PiperSay `
    -VoiceName $env:PIPER_VOICE_FR_NAME `
    -Text "Bonjour, je suis Carlson. Comment puis-je vous servir ?" `
    -OutWav (Join-Path $outDir "test-fr.wav")

Invoke-PiperSay `
    -VoiceName $env:PIPER_VOICE_EN_NAME `
    -Text "Good evening, I am Carlson. How may I help you today?" `
    -OutWav (Join-Path $outDir "test-en.wav")

Write-Ok "Piper opérationnel. Échantillons : $outDir"
Write-Host "     Lis-les avec : Invoke-Item '$outDir'" -ForegroundColor DarkGray
