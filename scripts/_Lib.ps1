# Butlr / Phase 1 — helpers communs PowerShell
# Dot-sourcé par tous les autres scripts via `. $PSScriptRoot\_Lib.ps1`.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Logging basique, coloré. Pas de dépendance externe.
# -----------------------------------------------------------------------------

function Write-Info {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[..] $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[ok] $Message" -ForegroundColor Green
}

function Write-Warn2 {
    # Write-Warning existe déjà ; on double pour un style homogène.
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[!!] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[KO] $Message" -ForegroundColor Red
}

# -----------------------------------------------------------------------------
# Assertions de prérequis
# -----------------------------------------------------------------------------

function Assert-Cmd {
    <#
    .SYNOPSIS
    Vérifie qu'une commande est disponible sur le PATH. Abort sinon.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Hint = ""
    )
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) {
        Write-Err "Commande introuvable : $Name"
        if ($Hint) { Write-Host "     -> $Hint" -ForegroundColor DarkGray }
        throw "Prérequis manquant : $Name"
    }
    Write-Ok "$Name trouvé ($($cmd.Source))"
}

function Test-PathExists {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$CreateIfMissing
    )
    if (Test-Path -LiteralPath $Path) { return $true }
    if ($CreateIfMissing) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        Write-Info "Créé : $Path"
        return $true
    }
    return $false
}

# -----------------------------------------------------------------------------
# Chargement de l'environnement
# -----------------------------------------------------------------------------

function Import-BtlrEnv {
    <#
    .SYNOPSIS
    Dot-source env.ps1 s'il existe, sinon env.example.ps1 avec un warning.
    À appeler au début de chaque script de setup.
    #>
    $dir = $PSScriptRoot
    $envFile     = Join-Path $dir "env.ps1"
    $envExample  = Join-Path $dir "env.example.ps1"

    if (Test-Path -LiteralPath $envFile) {
        . $envFile
        Write-Info "Environnement chargé depuis env.ps1"
    } elseif (Test-Path -LiteralPath $envExample) {
        . $envExample
        Write-Warn2 "env.ps1 absent — utilisation des valeurs par défaut (env.example.ps1). Copie env.example.ps1 -> env.ps1 pour customiser."
    } else {
        throw "Ni env.ps1 ni env.example.ps1 trouvés dans $dir"
    }

    # Sanity : les variables critiques sont-elles définies ?
    foreach ($v in @("BUTLR_ENV_DIR", "LLAMA_SRC_DIR", "MODELS_DIR", "VOICES_DIR")) {
        $val = [Environment]::GetEnvironmentVariable($v, "Process")
        if (-not $val) {
            throw "Variable d'environnement $v non définie après chargement env."
        }
    }
}

# -----------------------------------------------------------------------------
# Divers
# -----------------------------------------------------------------------------

function Invoke-NativeCommand {
    <#
    .SYNOPSIS
    Exécute une commande native et lève une exception si $LASTEXITCODE != 0.
    Utile après `curl.exe`, `cmake`, `git`, etc. car $ErrorActionPreference='Stop'
    ne s'applique pas aux exit codes non-zéro des binaires natifs.
    #>
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Args
    )
    & $Args[0] @($Args[1..($Args.Length - 1)])
    if ($LASTEXITCODE -ne 0) {
        throw "Commande native échouée (exit $LASTEXITCODE) : $($Args -join ' ')"
    }
}
