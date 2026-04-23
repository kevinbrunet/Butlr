# Butlr / Phase 1 — clone + build llama.cpp avec CUDA.
#
# Ce script DOIT être exécuté depuis "Developer PowerShell for VS 2022"
# pour que MSVC (cl.exe) soit sur le PATH. Sinon le build CUDA échouera.
#
# Sortie attendue : <LLAMA_SRC_DIR>\build\bin\Release\llama-server.exe
#
# Références :
#   - https://github.com/ggerganov/llama.cpp ✓
#   - Build instructions Windows + CUDA dans README du repo ~ (commandes
#     cmake exactes à confirmer au moment du build ; les flags bougent).

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Assert-Cmd -Name "git"
Assert-Cmd -Name "cmake"
Assert-Cmd -Name "nvcc"

$src = $env:LLAMA_SRC_DIR
Write-Info "Source llama.cpp : $src"

# -- Clone / pull ---------------------------------------------------------------
if (-not (Test-Path -LiteralPath (Join-Path $src ".git"))) {
    Write-Info "Clone du repo llama.cpp..."
    # -- On prend HEAD main. À épingler sur un tag stable avant prod (~).
    git clone "https://github.com/ggerganov/llama.cpp" $src
    if ($LASTEXITCODE -ne 0) { throw "git clone a échoué." }
} else {
    Write-Info "Repo déjà présent — git pull..."
    Push-Location $src
    try {
        git pull --ff-only
        if ($LASTEXITCODE -ne 0) { throw "git pull a échoué." }
    } finally {
        Pop-Location
    }
}

# -- Configure ------------------------------------------------------------------
$buildDir = Join-Path $src "build"
Write-Info "Configuration CMake (CUDA ON)..."

Push-Location $src
try {
    # GGML_CUDA=ON : bascule CUDA dans llama.cpp (ex-LLAMA_CUBLAS) ~
    # CMAKE_BUILD_TYPE=Release : implicite avec --config Release au build, mais on le répète.
    cmake -B $buildDir -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw "cmake configure a échoué." }

    Write-Info "Build (Release)..."
    # --parallel sans argument = tous les cœurs dispo.
    cmake --build $buildDir --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw "cmake build a échoué." }
} finally {
    Pop-Location
}

# -- Vérification de l'artefact -------------------------------------------------
$serverExe = Join-Path $buildDir "bin\Release\llama-server.exe"
if (-not (Test-Path -LiteralPath $serverExe)) {
    # Certaines versions posent l'exe dans build\bin\ directement (multi-config vs single-config).
    $alt = Join-Path $buildDir "bin\llama-server.exe"
    if (Test-Path -LiteralPath $alt) { $serverExe = $alt }
    else { throw "llama-server.exe introuvable après build. Cherche dans : $buildDir\bin\" }
}

Write-Ok "Build OK — llama-server : $serverExe"
Write-Host "     (mémorise ce chemin pour Start-LlamaServer.ps1)" -ForegroundColor DarkGray
