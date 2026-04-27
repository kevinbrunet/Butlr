#Requires -Version 7
<#
.SYNOPSIS
    Génère des clips synthétiques "Hey Carlson" pour chaque voix Piper trouvée dans VOICES_DIR.
    Chaque voix produit NClipsPerVoice clips nommés synth_<voice_name>_XXXX.wav.
    Sortie dans carlson/assets/wakeword/my_recordings/.

.DESCRIPTION
    Scanne VOICES_DIR pour tous les .onnx disponibles et génère NClipsPerVoice clips
    par voix avec variation de vitesse et prosodie. Le script est idempotent par voix :
    si synth_<voice_name>_0000.wav existe déjà, la voix est sautée.

    Note : supprimer les anciens synth_fr_XXXX.wav (nommage obsolète) avant de lancer
    si tu veux éviter de garder des doublons gilles-low.

.PARAMETER NClipsPerVoice
    Clips à générer par voix (défaut : 200).

.PARAMETER OutputDir
    Dossier de sortie. Défaut : carlson/assets/wakeword/my_recordings/.

.EXAMPLE
    .\Generate-FrenchTTS.ps1
    .\Generate-FrenchTTS.ps1 -NClipsPerVoice 300
#>
param(
    [int]$NClipsPerVoice = 200,
    [string]$OutputDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $PSScriptRoot\_Lib.ps1
Import-BtlrEnv

# ---------------------------------------------------------------------------
# Prérequis
# ---------------------------------------------------------------------------

$RepoRoot   = Split-Path $PSScriptRoot -Parent
$VenvPython = Join-Path $RepoRoot "carlson\.venv\Scripts\python.exe"

if (-not (Test-Path -LiteralPath $VenvPython)) {
    throw "venv Python introuvable : $VenvPython`nInstalle d'abord : cd carlson && python -m venv .venv && pip install -e .[all]"
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "carlson\assets\wakeword\my_recordings"
}
Test-PathExists -Path $OutputDir -CreateIfMissing | Out-Null

# Trouver tous les modèles .onnx (un par sous-dossier de VOICES_DIR)
$onnxFiles = Get-ChildItem -Path $env:VOICES_DIR -Recurse -Filter "*.onnx" |
    Where-Object { $_.Name -notlike "*.onnx.json" }

if ($onnxFiles.Count -eq 0) {
    throw "Aucun .onnx trouve dans $($env:VOICES_DIR).`nLance Get-PiperVoices.ps1 d'abord."
}

Write-Info "$($onnxFiles.Count) voix trouvees dans $($env:VOICES_DIR) :"
foreach ($f in $onnxFiles) { Write-Info "  $($f.Directory.Name)  ($($f.FullName))" }
Write-Host ""

# ---------------------------------------------------------------------------
# Génération par voix
# ---------------------------------------------------------------------------

$totalNew = 0

foreach ($onnxFile in $onnxFiles) {
    $VoicePath = $onnxFile.FullName
    $VoiceName = $onnxFile.Directory.Name   # ex. "fr_FR-gilles-low"
    $Prefix    = "synth_$VoiceName"         # ex. "synth_fr_FR-gilles-low"

    # Idempotence : si _0000.wav existe déjà pour cette voix, on saute
    $firstClip = Join-Path $OutputDir "$Prefix`_0000.wav"
    if (Test-Path -LiteralPath $firstClip) {
        Write-Warn2 "[$VoiceName] Deja present ($firstClip) — saute."
        continue
    }

    Write-Info "[$VoiceName] Generation de $NClipsPerVoice clips (prefix=$Prefix)..."

    $N = $NClipsPerVoice   # variable locale pour le here-string

    $PythonScript = @"
import dataclasses, inspect, random, wave, pathlib, sys

try:
    from piper.voice import PiperVoice
except ImportError:
    try:
        from piper import PiperVoice
    except ImportError:
        print("ERREUR : piper-tts non installe.", flush=True)
        sys.exit(1)

try:
    from piper.voice import SynthesisConfig
    cfg_fields = {f.name for f in dataclasses.fields(SynthesisConfig)}
except ImportError:
    SynthesisConfig = None
    cfg_fields = set()

voice       = PiperVoice.load(r'$VoicePath')
out_dir     = pathlib.Path(r'$OutputDir')
phrase      = "Hey Carlson"
n           = $N
prefix      = "$Prefix"
sample_rate = voice.config.sample_rate

sig_params     = list(inspect.signature(voice.synthesize).parameters.keys())
CFG_PARAM_NAME = next((p for p in sig_params if 'config' in p.lower()), None)
HAS_WAV_PARAM  = any(p in sig_params for p in ('wav_file', 'wav', 'file', 'output', 'output_file'))
HAS_CFG_PARAM  = SynthesisConfig is not None and CFG_PARAM_NAME is not None

def make_cfg_kwargs():
    if not HAS_CFG_PARAM:
        return {}
    vals = {}
    for fname, lo, hi in [
        ('length_scale',  0.85, 1.15),
        ('noise_scale',   0.55, 0.75),
        ('noise_w',       0.7,  0.9),
        ('noise_w_scale', 0.7,  0.9),
    ]:
        if fname in cfg_fields:
            vals[fname] = random.uniform(lo, hi)
    return {CFG_PARAM_NAME: SynthesisConfig(**vals)}

def chunk_to_bytes(chunk):
    if isinstance(chunk, (bytes, bytearray)):
        return chunk
    for attr in ('audio_int16_bytes', 'audio', 'data', 'samples', 'frames', 'pcm'):
        val = getattr(chunk, attr, None)
        if val is None:
            continue
        if isinstance(val, (bytes, bytearray)):
            return val
        if hasattr(val, 'tobytes'):
            return val.tobytes()
    raise AttributeError(f"Impossible d extraire bytes de {type(chunk).__name__}: {[a for a in dir(chunk) if not a.startswith('_')]}")

TARGET_RATE = 16000

def resample_to_16k(raw_bytes, src_rate):
    """Resampling vers 16 kHz via scipy.signal.resample_poly (qualite sinc)."""
    if src_rate == TARGET_RATE:
        return raw_bytes
    import numpy as np
    from math import gcd
    from scipy.signal import resample_poly
    samples = np.frombuffer(raw_bytes, dtype=np.int16).astype(np.float32) / 32767.0
    g = gcd(TARGET_RATE, src_rate)
    resampled = resample_poly(samples, TARGET_RATE // g, src_rate // g)
    return (np.clip(resampled, -1.0, 1.0) * 32767).astype(np.int16).tobytes()

generated = 0
for i in range(n):
    name  = f"{prefix}_{i:04d}.wav"
    extra = make_cfg_kwargs()
    if HAS_WAV_PARAM:
        with wave.open(str(out_dir / name), "wb") as wf:
            voice.synthesize(phrase, wf, **extra)
    else:
        audio_iter = voice.synthesize(phrase, **extra)
        raw = b"".join(chunk_to_bytes(c) for c in audio_iter)
        raw = resample_to_16k(raw, sample_rate)
        with wave.open(str(out_dir / name), "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(TARGET_RATE)
            wf.writeframes(raw)
    generated += 1
    if generated % 50 == 0:
        print(f"  {generated}/{n}...", flush=True)

effective_rate = TARGET_RATE if sample_rate != TARGET_RATE else sample_rate
print(f"OK : {generated} clips a {effective_rate} Hz (source={sample_rate} Hz)", flush=True)
"@

    & $VenvPython -c $PythonScript
    if ($LASTEXITCODE -ne 0) {
        throw "Generation echouee pour $VoiceName (exit $LASTEXITCODE)"
    }

    $totalNew += $NClipsPerVoice
    Write-Ok "[$VoiceName] $NClipsPerVoice clips generes."
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Résumé
# ---------------------------------------------------------------------------

$totalWav = (Get-ChildItem -Path $OutputDir -Filter "synth_*.wav").Count
Write-Ok "$totalNew nouveaux clips synthetiques ($totalWav synth_* au total dans $OutputDir)"
Write-Host ""
Write-Info "Si des anciens synth_fr_XXXX.wav (nommage obsolete) sont presents, tu peux les supprimer :"
Write-Info "  Remove-Item $OutputDir\synth_fr_????.wav"
Write-Host ""
Write-Info "Prochaine etape : .\Train-WakeWord.ps1 -RebuildImage"
