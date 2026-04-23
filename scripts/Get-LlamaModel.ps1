# Butlr / Phase 1 — télécharge le GGUF Qwen 2.5 7B Instruct Q5_K_M.
#
# Taille attendue : ~5,4 GB ~. curl.exe est préféré à Invoke-WebRequest pour
# ce volume (progression, resume avec -C -, plus robuste sur les HF redirects).

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Assert-Cmd -Name "curl.exe"

$url  = $env:LLAMA_MODEL_URL
$dest = $env:LLAMA_MODEL_FILE

Test-PathExists -Path (Split-Path -Parent $dest) -CreateIfMissing | Out-Null

if (Test-Path -LiteralPath $dest) {
    $sizeGB = [math]::Round((Get-Item -LiteralPath $dest).Length / 1GB, 2)
    Write-Warn2 "Fichier déjà présent : $dest ($sizeGB GB)"
    Write-Host "     Supprime manuellement si tu veux re-télécharger." -ForegroundColor DarkGray
    return
}

Write-Info "Téléchargement GGUF depuis : $url"
Write-Info "Destination               : $dest"

# -L : suivre les redirects (HF renvoie vers un CDN).
# -C - : resume si interrompu.
# -# : barre de progression concise.
# --fail : exit != 0 sur HTTP 4xx/5xx (défaut = 0, piège).
curl.exe -L -C - -# --fail -o $dest $url
if ($LASTEXITCODE -ne 0) {
    throw "Download GGUF échoué (curl exit $LASTEXITCODE)."
}

$sizeGB = [math]::Round((Get-Item -LiteralPath $dest).Length / 1GB, 2)
Write-Ok "GGUF téléchargé : $dest ($sizeGB GB)"
