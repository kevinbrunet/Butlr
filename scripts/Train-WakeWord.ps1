# Butlr / Phase 4 — Entraîne le wake word "Hey Carlson" via Docker.
#
# Lance un container Linux (openWakeWord + Piper TTS) qui :
#   1. Génère ~5 000 clips audio "Hey Carlson" via Piper TTS
#   2. Télécharge les données négatives depuis Hugging Face
#   3. Entraîne le modèle et produit hey_carlson.tflite
#
# Prérequis : Docker Desktop installé et en cours d'exécution.
# GPU       : si NVIDIA Container Toolkit configuré dans Docker Desktop,
#             passer -Gpu pour accélérer l'entraînement (~45 min vs ~4 h CPU).
#
# Usage :
#   .\Train-WakeWord.ps1                  # entraînement CPU
#   .\Train-WakeWord.ps1 -Gpu             # entraînement GPU (NVIDIA requis)
#   .\Train-WakeWord.ps1 -GenerateConfig  # génère le YAML de config seulement
#   .\Train-WakeWord.ps1 -RebuildImage    # force le rebuild de l'image Docker

#Requires -Version 7
param(
    [switch] $Gpu,
    [switch] $GenerateConfig,
    [switch] $RebuildImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

$repoRoot    = Split-Path $PSScriptRoot -Parent
$carlsonDir  = Join-Path $repoRoot 'carlson'
$assetsDir   = Join-Path $carlsonDir 'assets\wakeword'
$configPath  = Join-Path $assetsDir 'training_config.yaml'
# Cache des features HuggingFace (~4-6 GB) — persistant entre les runs pour éviter
# le re-téléchargement. Stocké hors du repo (trop volumineux pour git).
$featuresDir = Join-Path $env:LOCALAPPDATA 'Butlr\wakeword-features'
$imageName   = 'butlr-wakeword-train'
$dockerfile  = 'carlson/docker/Dockerfile.wakeword-train'

# -- Vérifie Docker ------------------------------------------------------------
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker introuvable dans PATH. Installe Docker Desktop : https://www.docker.com/products/docker-desktop/"
}

$dockerRunning = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker daemon ne répond pas. Lance Docker Desktop et réessaie."
}

# -- Mode : génération de config seule -----------------------------------------
if ($GenerateConfig) {
    Write-Host "Génération du fichier de config YAML..." -ForegroundColor Cyan

    $venvPython = Join-Path $carlsonDir '.venv\Scripts\python.exe'
    $trainScript = Join-Path $carlsonDir 'scripts\train_wakeword.py'

    if (-not (Test-Path -LiteralPath $venvPython)) {
        Write-Error "venv carlson introuvable : $venvPython`nCrée-le d'abord avec pip install -e '.[all,dev]'"
    }

    & $venvPython $trainScript --generate-config
    if ($LASTEXITCODE -ne 0) { Write-Error "Génération de config échouée." }

    Write-Host "`nConfig prête : $configPath" -ForegroundColor Green
    Write-Host "Lance l'entraînement : .\Train-WakeWord.ps1"
    return
}

# -- Génère la config si absente -----------------------------------------------
if (-not (Test-Path -LiteralPath $configPath)) {
    Write-Host "training_config.yaml absent — génération automatique..." -ForegroundColor Yellow

    $venvPython  = Join-Path $carlsonDir '.venv\Scripts\python.exe'
    $trainScript = Join-Path $carlsonDir 'scripts\train_wakeword.py'

    if (Test-Path -LiteralPath $venvPython) {
        & $venvPython $trainScript --generate-config
        if ($LASTEXITCODE -ne 0) { Write-Error "Génération de config échouée." }
    } else {
        # Écrit le YAML minimal sans Python
        New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
        @'
model_name: hey_carlson
target_phrase: "Hey Carlson"
n_positive_samples: 5000
n_epochs: 100
detection_threshold: 0.5
use_precomputed_features: true
'@ | Set-Content -Path $configPath -Encoding UTF8
        Write-Host "Config minimale écrite dans $configPath" -ForegroundColor Yellow
    }
}

# -- Build de l'image Docker ---------------------------------------------------
$imageExists = docker image inspect $imageName 2>&1
$needsBuild  = ($LASTEXITCODE -ne 0) -or $RebuildImage

if ($needsBuild) {
    Write-Host "Build de l'image Docker $imageName..." -ForegroundColor Cyan
    Write-Host "  (~5-10 min, ~4-6 GB téléchargés — uniquement au premier build)" -ForegroundColor DarkGray

    Push-Location $repoRoot
    try {
        docker build -f $dockerfile -t $imageName .
        if ($LASTEXITCODE -ne 0) { Write-Error "docker build échoué." }
    } finally {
        Pop-Location
    }

    Write-Host "Image construite : $imageName" -ForegroundColor Green
} else {
    Write-Host "Image $imageName déjà présente (utilise -RebuildImage pour forcer)." -ForegroundColor DarkGray
}

# -- Lancement de l'entraînement -----------------------------------------------
$assetsDirAbs   = [System.IO.Path]::GetFullPath($assetsDir)
$featuresDirAbs = [System.IO.Path]::GetFullPath($featuresDir)
New-Item -ItemType Directory -Force -Path $featuresDirAbs | Out-Null

$dockerArgs = @(
    'run', '--rm',
    '--name', 'butlr-wakeword-train',
    '-v', "${assetsDirAbs}:/data",
    '-v', "${featuresDirAbs}:/work/features",
    $imageName
)

if ($Gpu) {
    Write-Host "Mode GPU activé (--gpus all)." -ForegroundColor Cyan
    $dockerArgs = @(
        'run', '--rm', '--gpus', 'all',
        '--name', 'butlr-wakeword-train',
        '-v', "${assetsDirAbs}:/data",
        '-v', "${featuresDirAbs}:/work/features",
        $imageName
    )
}

Write-Host ""
Write-Host "Lancement de l'entraînement..." -ForegroundColor Cyan
Write-Host "  Config    : $configPath"
Write-Host "  Features  : $featuresDirAbs (cache persistant, ~4-6 GB au 1er run)"
Write-Host "  Sortie    : $assetsDir\hey_carlson.tflite"
if ($Gpu) {
    Write-Host "  Durée     : ~45 min (GPU)" -ForegroundColor DarkGray
} else {
    Write-Host "  Durée     : ~2-4 h (CPU) — ajoute -Gpu si tu as NVIDIA Container Toolkit" -ForegroundColor DarkGray
}
Write-Host ""

docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "L'entraînement a échoué (exit $LASTEXITCODE). Vérifie les logs ci-dessus."
}

# -- Résultat ------------------------------------------------------------------
$tflite = Join-Path $assetsDir 'hey_carlson.tflite'
if (Test-Path -LiteralPath $tflite) {
    $sizeKb = [int]((Get-Item $tflite).Length / 1024)
    Write-Host ""
    Write-Host "Modèle prêt : $tflite ($sizeKb KB)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Active le wake word :"
    Write-Host "  `$env:USE_WAKEWORD = '1'"
    Write-Host "  carlson"
} else {
    Write-Warning "hey_carlson.tflite introuvable après l'entraînement. Vérifie les logs."
}
