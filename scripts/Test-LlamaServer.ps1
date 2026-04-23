# Butlr / Phase 1 — smoke test HTTP du llama-server lancé.
#
# À exécuter dans un second terminal après Start-LlamaServer.ps1.
# Vérifie :
#   1. /v1/models répond 200 avec un JSON contenant au moins un modèle.
#   2. /v1/chat/completions renvoie une complétion non vide sur un prompt court.

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Assert-Cmd -Name "curl.exe"

$base = "http://localhost:$($env:LLAMA_PORT)"

# -- /v1/models -----------------------------------------------------------------
Write-Info "GET $base/v1/models"
$modelsRaw = curl.exe -s --fail "$base/v1/models"
if ($LASTEXITCODE -ne 0) {
    throw "llama-server injoignable sur $base (curl exit $LASTEXITCODE). Il tourne ?"
}

try {
    $models = $modelsRaw | ConvertFrom-Json
} catch {
    Write-Err "Réponse non-JSON : $modelsRaw"
    throw
}

if (-not $models.data -or $models.data.Count -eq 0) {
    throw "Aucun modèle listé par le serveur."
}
Write-Ok "Modèle servi : $($models.data[0].id)"

# -- /v1/chat/completions -------------------------------------------------------
Write-Info "POST $base/v1/chat/completions (prompt court)"

$payload = @{
    model    = $models.data[0].id
    messages = @(
        @{ role = "user"; content = "Réponds juste 'pong'." }
    )
    max_tokens  = 32
    temperature = 0
} | ConvertTo-Json -Compress

# curl.exe -d @- lit depuis stdin — plus fiable que passer un JSON inline en CLI
# (le quoting PowerShell/curl est un champ de mines).
$resp = $payload | curl.exe -s --fail -X POST "$base/v1/chat/completions" `
    -H "Content-Type: application/json" `
    --data-binary "@-"

if ($LASTEXITCODE -ne 0) {
    throw "chat/completions échoué (curl exit $LASTEXITCODE)."
}

$json = $resp | ConvertFrom-Json
$content = $json.choices[0].message.content
if (-not $content) {
    throw "Réponse vide ou structure inattendue. Raw : $resp"
}

Write-Ok "Réponse : $content"
Write-Ok "llama-server opérationnel ✓"
