# Butlr / Phase 1 — lance llama-server en foreground.
#
# Expose /v1/chat/completions et /v1/completions en OpenAI-compat (cf. ADR 0006).
# Carlson pointera dessus via LLM_BASE_URL=http://localhost:8080/v1.
#
# Pour arrêter : Ctrl+C.

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

# -- Localise llama-server.exe --------------------------------------------------
$buildDir = Join-Path $env:LLAMA_SRC_DIR "build"
$candidates = @(
    (Join-Path $buildDir "bin\Release\llama-server.exe"),
    (Join-Path $buildDir "bin\llama-server.exe")
)

$serverExe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $serverExe) {
    Write-Err "llama-server.exe introuvable. Lance d'abord Build-Llama.ps1."
    Write-Host "Cherché dans :" -ForegroundColor DarkGray
    $candidates | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
    throw "llama-server manquant."
}

# -- Vérifie le modèle ----------------------------------------------------------
if (-not (Test-Path -LiteralPath $env:LLAMA_MODEL_FILE)) {
    Write-Err "Modèle GGUF absent : $env:LLAMA_MODEL_FILE"
    Write-Host "Lance d'abord Get-LlamaModel.ps1" -ForegroundColor DarkGray
    throw "Modèle manquant."
}

# -- Monte la commande ----------------------------------------------------------
# Flags :
#   -m <path>         : GGUF à charger
#   -ngl <n>          : nb de layers offload GPU (99 = tout)
#   -c   <n>          : taille de contexte
#   --host / --port   : binding HTTP
#   $LLAMA_EXTRA_FLAGS: ex. --jinja pour tool calling OpenAI-compat ~
$argList = @(
    "-m", $env:LLAMA_MODEL_FILE,
    "-ngl", $env:LLAMA_NGL,
    "-c",   $env:LLAMA_CTX,
    "--host", $env:LLAMA_HOST,
    "--port", $env:LLAMA_PORT
)

if ($env:LLAMA_EXTRA_FLAGS) {
    # Split basique sur espaces — suffisant pour "--jinja" ou "--jinja --mlock".
    $argList += ($env:LLAMA_EXTRA_FLAGS -split '\s+' | Where-Object { $_ })
}

Write-Info "Lancement llama-server :"
Write-Host "     $serverExe $($argList -join ' ')" -ForegroundColor DarkGray
Write-Host ""
Write-Info "URL OpenAI-compat : http://$($env:LLAMA_HOST):$($env:LLAMA_PORT)/v1"
Write-Info "Ctrl+C pour arrêter."
Write-Host ""

& $serverExe @argList
