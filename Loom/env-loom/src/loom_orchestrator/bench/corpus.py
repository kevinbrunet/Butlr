from __future__ import annotations

import wave
from dataclasses import dataclass
from pathlib import Path

# Les scripts de bench sont prévus pour être lancés depuis la racine Loom/ — cf. Loom/CLAUDE.md.
CORPUS_DIR = Path("corpus")

EXPECTED_SAMPLE_RATE_HZ = 16_000
# ✓ whisperlivekit/audio_processor.py hardcode bytes_per_sample=2 (PCM 16 bits) pour le mode
# pcm_input — vérifié par lecture du code source (repo QuentinFuxa/WhisperLiveKit, 2026-07-07).
EXPECTED_SAMPLE_WIDTH_BYTES = 2


@dataclass(frozen=True)
class CorpusFile:
    key: str
    filename: str
    language: str
    speakers: int
    min_duration_s: float
    description: str
    provenance: str


# T0.2 du backlog : 6 fichiers wav 16kHz, versionnés dans corpus/ (choix délibéré malgré le
# binaire, pour que le corpus voyage avec le repo entre postes/machine d'exécution).
#
# Provenance (2026-07-07) — audio public domain / recherche, converti en 16kHz mono PCM16 via
# ffmpeg, validé par `validate()` contre chaque fichier réel :
# - a, b : LibriVox (archive.org), lecture publique du domaine public.
# - c : LibriVox (archive.org), collection en mandarin (langue "zho").
# - d : George Mason University Speech Accent Archive (accent.gmu.edu), corpus académique de
#   locuteurs non-natifs lisant un paragraphe standardisé en anglais — usage recherche/éducatif.
#   Fichier source (~32s) bouclé pour atteindre min_duration_s (mêmes propos, même locuteur).
#
# Provenance (2026-07-15) — e, f : mêmes deux locuteurs et le même chevauchement que `b`
# (comparaison à bruit constant, seul le bruit de fond change), plus un bruit d'ambiance réel
# mixé par-dessus (script pur Python, RMS scalé pour un SNR cible de 10dB — pas de source
# externe pour ce chiffre, choix d'ingénierie pour rester "bruyant mais exploitable", à ajuster
# si les runs WLK le rendent trop dégradé). Bruit source : DEMAND (Diverse Environments
# Multichannel Acoustic Noise Database, zenodo.org/records/1227121), licence CC BY-SA 3.0 —
# ✓ catégories PCAFETER (cafétéria animée) et PRESTO (restaurant universitaire à l'heure du
# déjeuner), un seul canal (ch01) extrait des enregistrements 16 canaux, déjà nativement 16kHz
# mono, pas de resample nécessaire.
CORPUS_MANIFEST: tuple[CorpusFile, ...] = (
    CorpusFile(
        "a", "a_en_mono.wav", "en", 1, 180.0, "EN mono-locuteur, ~3 min",
        provenance="archive.org/details/alice_in_wonderland_librivox, ch.1 (Lewis Carroll, "
        "Alice's Adventures in Wonderland), domaine public.",
    ),
    CorpusFile(
        "b", "b_en_overlap.wav", "en", 2, 60.0, "EN 2 locuteurs avec chevauchements",
        provenance="Mix synthétique (ffmpeg amix+adelay, chevauchement 20-65s) de "
        "archive.org/details/tom_sawyer_librivox ch.1-2 (Mark Twain) et "
        "archive.org/details/moby_dick_librivox ch.1-2 (Herman Melville), domaine public.",
    ),
    CorpusFile(
        "c", "c_zh_mono.wav", "zh", 1, 60.0, "ZH mono-locuteur",
        provenance="archive.org/details/call_to_arms_jl_librivox, ch.7 (Lu Xun, 呐喊), "
        "domaine public.",
    ),
    CorpusFile(
        "d", "d_en_accented.wav", "en", 1, 60.0, "EN, accents non-natifs",
        provenance="accent.gmu.edu/soundtracks/mandarin1.mp3 (Speech Accent Archive, GMU) — "
        "locuteur natif mandarin lisant le paragraphe standard en anglais, bouclé pour la durée.",
    ),
    CorpusFile(
        "e", "e_en_overlap_cafe.wav", "en", 2, 60.0,
        "EN 2 locuteurs avec chevauchements + bruit de cafétéria (SNR ~10dB)",
        provenance="b_en_overlap.wav (mêmes locuteurs/chevauchement) + bruit DEMAND PCAFETER_16k "
        "(zenodo.org/records/1227121, CC BY-SA 3.0), mixé par script Python (RMS, SNR cible 10dB).",
    ),
    CorpusFile(
        "f", "f_en_overlap_resto.wav", "en", 2, 60.0,
        "EN 2 locuteurs avec chevauchements + bruit de restaurant (SNR ~10dB)",
        provenance="b_en_overlap.wav (mêmes locuteurs/chevauchement) + bruit DEMAND PRESTO_16k "
        "(zenodo.org/records/1227121, CC BY-SA 3.0), mixé par script Python (RMS, SNR cible 10dB).",
    ),
)


class CorpusValidationError(Exception):
    pass


def _entry(key: str) -> CorpusFile:
    entry = next((c for c in CORPUS_MANIFEST if c.key == key), None)
    if entry is None:
        known = [c.key for c in CORPUS_MANIFEST]
        raise KeyError(f"clé de corpus inconnue : {key!r} — attendu un de {known}")
    return entry


def resolve(key: str, corpus_dir: Path = CORPUS_DIR) -> Path:
    return corpus_dir / _entry(key).filename


def validate(key: str, corpus_dir: Path = CORPUS_DIR) -> None:
    """Vérifie qu'un fichier du corpus existe et respecte le format attendu (16kHz mono, PCM 16 bits).

    Ne vérifie ni la langue ni le contenu réel (pas de détection auto ici) — seulement le
    format audio, condition nécessaire pour un replay temps réel fidèle (T0.2).
    """
    entry = _entry(key)
    path = corpus_dir / entry.filename
    if not path.exists():
        raise CorpusValidationError(f"fichier corpus manquant : {path}")

    with wave.open(str(path), "rb") as wav_file:
        if wav_file.getframerate() != EXPECTED_SAMPLE_RATE_HZ:
            raise CorpusValidationError(
                f"{path} : {wav_file.getframerate()}Hz, attendu {EXPECTED_SAMPLE_RATE_HZ}Hz"
            )
        if wav_file.getnchannels() != 1:
            raise CorpusValidationError(
                f"{path} : {wav_file.getnchannels()} canaux, attendu mono (1)"
            )
        if wav_file.getsampwidth() != EXPECTED_SAMPLE_WIDTH_BYTES:
            raise CorpusValidationError(
                f"{path} : PCM {wav_file.getsampwidth() * 8} bits, "
                f"attendu {EXPECTED_SAMPLE_WIDTH_BYTES * 8} bits (WLK en mode pcm_input)"
            )
        duration_s = wav_file.getnframes() / wav_file.getframerate()
        if duration_s < entry.min_duration_s:
            raise CorpusValidationError(
                f"{path} : {duration_s:.1f}s, attendu au moins {entry.min_duration_s:.0f}s"
            )
