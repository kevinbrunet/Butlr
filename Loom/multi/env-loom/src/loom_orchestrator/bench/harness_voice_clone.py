from __future__ import annotations

import argparse
from pathlib import Path

from loom_orchestrator.bench import corpus
from loom_orchestrator.bench.audio_chunks import read_segment
from loom_orchestrator.tts_pocket import PocketTtsSynthesizer

# ✓ Même phrase FR que les increments réels utilisés dans `harness_tts_continuation.py`
# (pas un texte de test synthétique) — pour rester comparable au reste des sondes TTS.
TEST_SENTENCE = (
    "Alice commençait à s'ennuyer beaucoup de rester assise auprès de sa sœur sur la rive "
    "et d'avoir rien à faire."
)


def _write_wav(path: Path, chunks: list, sample_rate_hz: int) -> None:
    import wave

    import numpy as np

    audio = np.concatenate(chunks) if chunks else np.zeros(0, dtype=np.float32)
    pcm16 = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate_hz)
        wav_file.writeframes(pcm16.tobytes())


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Sonde isolée (ADR-0046) : clone une voix depuis un seul clip audio "
        "source continu (pas des petits bouts de ~1s concaténés comme le fait "
        "voice_personalization.PersonalizedVoiceManager en usage réel) — pour savoir si le "
        "problème de qualité observé sur un run réel (bruit/silence/instabilité) vient du "
        "clonage lui-même ou de la façon dont l'audio source est découpé/accumulé en amont."
    )
    parser.add_argument("--corpus-key", default="a")
    parser.add_argument("--corpus-dir", type=Path, default=corpus.CORPUS_DIR)
    parser.add_argument(
        "--start-s", type=float, default=5.0, help="Début du clip source (défaut : 5.0s)."
    )
    parser.add_argument(
        "--end-s", type=float, default=20.0, help="Fin du clip source (défaut : 20.0s)."
    )
    parser.add_argument("--out-wav", type=Path, default=Path("/tmp/harness-voice-clone.wav"))
    parser.add_argument(
        "--out-wav-default", type=Path, default=Path("/tmp/harness-voice-clone-default.wav"),
        help="Même phrase avec la voix de repli du constructeur, pour comparaison à l'oreille.",
    )
    args = parser.parse_args()

    corpus.validate(args.corpus_key, corpus_dir=args.corpus_dir)
    wav_path = corpus.resolve(args.corpus_key, corpus_dir=args.corpus_dir)

    print(f"Extraction de [{args.start_s}s, {args.end_s}s) depuis {wav_path}")
    segment = read_segment(wav_path, args.start_s, args.end_s)
    print(f"Clip source : {len(segment)} échantillons, {len(segment) / 16_000:.1f}s à 16kHz")

    synth = PocketTtsSynthesizer()

    print("Clonage de la voix...")
    cloned_state = synth.clone_voice_state(segment, source_sample_rate_hz=16_000)

    print("Synthèse avec la voix clonée...")
    cloned_chunks = list(synth.synthesize_stream(TEST_SENTENCE, cloned_state))
    _write_wav(args.out_wav, cloned_chunks, synth.sample_rate_hz)
    print(f"WAV (voix clonée) écrit : {args.out_wav}")

    print("Synthèse avec la voix de repli (comparaison)...")
    default_chunks = list(synth.synthesize_stream(TEST_SENTENCE))
    _write_wav(args.out_wav_default, default_chunks, synth.sample_rate_hz)
    print(f"WAV (voix de repli) écrit : {args.out_wav_default}")


if __name__ == "__main__":
    main()
