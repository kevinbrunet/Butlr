#!/usr/bin/env bash
# Butlr — génère des clips synthétiques "Hey Carson" pour chaque voix Piper.
#
# Chaque voix produit NClipsPerVoice clips nommés synth_<voice_name>_XXXX.wav.
# Sortie dans carson/assets/wakeword/my_recordings/.
# Idempotent par voix : si synth_<voice_name>_0000.wav existe, la voix est sautée.
#
# Usage :
#   ./generate-french-tts.sh               # 200 clips par voix
#   ./generate-french-tts.sh -n 300        # 300 clips par voix
#   ./generate-french-tts.sh -o /chemin/   # dossier de sortie custom

set -euo pipefail
. "$(dirname "$(realpath "$0")")/_lib.sh"
import_btlr_env

n_clips=200
output_dir=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -n|--n-clips)    n_clips="$2";    shift 2 ;;
        -o|--output-dir) output_dir="$2"; shift 2 ;;
        *) log_err "Argument inconnu : $1"; exit 1 ;;
    esac
done

repo_root="$(realpath "$(dirname "$(realpath "$0")")/..")"
venv_python="$repo_root/carson/.venv/bin/python"

if [ ! -f "$venv_python" ]; then
    log_err "venv Python introuvable : $venv_python"
    log_gray "Installe d'abord : cd carson && python3 -m venv .venv && pip install -e '.[all]'"
    exit 1
fi

if [ -z "$output_dir" ]; then
    output_dir="$repo_root/carson/assets/wakeword/my_recordings"
fi
ensure_dir "$output_dir"

# Trouver tous les .onnx (exclure .onnx.json)
mapfile -t onnx_files < <(find "$VOICES_DIR" -name "*.onnx" ! -name "*.onnx.json" 2>/dev/null | sort)

if [ ${#onnx_files[@]} -eq 0 ]; then
    log_err "Aucun .onnx trouvé dans $VOICES_DIR."
    log_gray "Lance ./get-piper-voices.sh d'abord."
    exit 1
fi

log_info "${#onnx_files[@]} voix trouvées dans $VOICES_DIR :"
for f in "${onnx_files[@]}"; do
    log_info "  $(basename "$(dirname "$f")")  ($f)"
done
echo ""

total_new=0

for onnx_file in "${onnx_files[@]}"; do
    voice_name="$(basename "$(dirname "$onnx_file")")"
    prefix="synth_${voice_name}"
    first_clip="${output_dir}/${prefix}_0000.wav"

    if [ -f "$first_clip" ]; then
        log_warn "[$voice_name] Déjà présent ($first_clip) — saute."
        continue
    fi

    log_info "[$voice_name] Génération de $n_clips clips (prefix=$prefix)..."

    tmp_script=$(mktemp /tmp/btlr-tts-XXXXXX.py)
    # shellcheck disable=SC2064
    trap "rm -f '$tmp_script'" EXIT

    # Le heredoc est délimité par PYEOF non quoté : bash substitue les variables
    # shell ($onnx_file, $output_dir, $n_clips, $prefix) mais laisse intact
    # tout ce qui ne commence pas par $ (f-strings Python, etc.).
    cat > "$tmp_script" <<PYEOF
import dataclasses, inspect, random, wave, pathlib, sys

try:
    from piper.voice import PiperVoice
except ImportError:
    try:
        from piper import PiperVoice
    except ImportError:
        print("ERREUR : piper-tts non installé.", flush=True)
        sys.exit(1)

try:
    from piper.voice import SynthesisConfig
    cfg_fields = {f.name for f in dataclasses.fields(SynthesisConfig)}
except ImportError:
    SynthesisConfig = None
    cfg_fields = set()

voice       = PiperVoice.load("${onnx_file}")
out_dir     = pathlib.Path("${output_dir}")
phrase      = "Hey Carson"
n           = ${n_clips}
prefix      = "${prefix}"
sample_rate = voice.config.sample_rate

sig_params     = list(inspect.signature(voice.synthesize).parameters.keys())
CFG_PARAM_NAME = next((p for p in sig_params if "config" in p.lower()), None)
HAS_WAV_PARAM  = any(p in sig_params for p in ("wav_file", "wav", "file", "output", "output_file"))
HAS_CFG_PARAM  = SynthesisConfig is not None and CFG_PARAM_NAME is not None

def make_cfg_kwargs():
    if not HAS_CFG_PARAM:
        return {}
    vals = {}
    for fname, lo, hi in [
        ("length_scale",  0.85, 1.15),
        ("noise_scale",   0.55, 0.75),
        ("noise_w",       0.7,  0.9),
        ("noise_w_scale", 0.7,  0.9),
    ]:
        if fname in cfg_fields:
            vals[fname] = random.uniform(lo, hi)
    return {CFG_PARAM_NAME: SynthesisConfig(**vals)}

def chunk_to_bytes(chunk):
    if isinstance(chunk, (bytes, bytearray)):
        return chunk
    for attr in ("audio_int16_bytes", "audio", "data", "samples", "frames", "pcm"):
        val = getattr(chunk, attr, None)
        if val is None:
            continue
        if isinstance(val, (bytes, bytearray)):
            return val
        if hasattr(val, "tobytes"):
            return val.tobytes()
    raise AttributeError(f"Impossible d'extraire bytes de {type(chunk).__name__}: {[a for a in dir(chunk) if not a.startswith('_')]}")

TARGET_RATE = 16000

def resample_to_16k(raw_bytes, src_rate):
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
print(f"OK : {generated} clips à {effective_rate} Hz (source={sample_rate} Hz)", flush=True)
PYEOF

    "$venv_python" "$tmp_script"

    total_new=$((total_new + n_clips))
    log_ok "[$voice_name] $n_clips clips générés."
    echo ""
done

total_wav=$(find "$output_dir" -name "synth_*.wav" | wc -l)
log_ok "$total_new nouveaux clips synthétiques ($total_wav synth_* au total dans $output_dir)"
echo ""
log_info "Prochaine étape : ./train-wakeword.sh"
