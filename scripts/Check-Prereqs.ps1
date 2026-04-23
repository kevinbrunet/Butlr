# Butlr / Phase 1 — vérifie les prérequis système avant de builder llama.cpp
# et d'installer le reste de la stack (Whisper, Piper).
#
# Prérequis attendus :
#   - Windows 10/11 ✓
#   - GPU NVIDIA avec drivers récents ✓
#   - CUDA Toolkit installé (nvcc sur PATH) ~ — version à aligner avec la toolchain llama.cpp
#   - Visual Studio 2022 + Desktop C++ workload (cl.exe sur PATH quand on lance
#     depuis "Developer PowerShell for VS 2022") ✓
#   - cmake, git, python, curl.exe ✓
#
# Usage :
#   cd scripts
#   .\Check-Prereqs.ps1

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

Write-Info "Vérification des prérequis..."

# -- GPU / CUDA -----------------------------------------------------------------
Assert-Cmd -Name "nvidia-smi" -Hint "Installer les drivers NVIDIA récents : https://www.nvidia.com/Download/index.aspx"
Assert-Cmd -Name "nvcc"       -Hint "Installer CUDA Toolkit : https://developer.nvidia.com/cuda-downloads (et relancer le shell)"

# Affiche la version CUDA runtime et le GPU pour vérif visuelle rapide.
Write-Info "GPU / drivers :"
nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader | ForEach-Object {
    Write-Host "     $_" -ForegroundColor DarkGray
}

Write-Info "CUDA toolkit :"
$nvccVersion = (nvcc --version) -join "`n"
Write-Host ($nvccVersion -split "`n" | Where-Object { $_ -match "release" }) -ForegroundColor DarkGray

# -- Toolchain C++ --------------------------------------------------------------
Assert-Cmd -Name "cmake" -Hint "Installer CMake >= 3.24 : https://cmake.org/download/ ou via Visual Studio Installer"
Assert-Cmd -Name "git"   -Hint "Installer Git : https://git-scm.com/download/win"

# cl.exe n'est présent que dans un shell Developer PowerShell for VS.
# On warn plutôt que throw, au cas où l'utilisateur aurait configuré un autre générateur.
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if ($cl) {
    Write-Ok "cl.exe trouvé ($($cl.Source)) — Developer PowerShell actif."
} else {
    Write-Warn2 "cl.exe introuvable. Lance ce script depuis 'Developer PowerShell for VS 2022' pour le build CUDA de llama.cpp."
}

# -- Python ---------------------------------------------------------------------
Assert-Cmd -Name "python" -Hint "Installer Python 3.11+ : https://www.python.org/downloads/windows/"

$pyVersionRaw = (python --version) 2>&1
Write-Info "Python : $pyVersionRaw"

# -- Réseau / download ----------------------------------------------------------
# curl.exe natif (Windows 10+ 1803+) — on préfère à Invoke-WebRequest pour les
# gros fichiers (GGUF 5-6 GB) car plus robuste et progression visible.
Assert-Cmd -Name "curl.exe" -Hint "Normalement fourni par Windows 10 1803+. Sinon : https://curl.se/windows/"

# -- Dossiers d'environnement ---------------------------------------------------
Write-Info "Dossiers Butlr :"
foreach ($d in @($env:BUTLR_ENV_DIR, $env:LLAMA_SRC_DIR, $env:MODELS_DIR, $env:VOICES_DIR)) {
    Test-PathExists -Path $d -CreateIfMissing | Out-Null
    Write-Host "     $d" -ForegroundColor DarkGray
}

Write-Ok "Tous les prérequis sont OK."
