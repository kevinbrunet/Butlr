from __future__ import annotations

import numpy as np

from loom_orchestrator.speaker_identity import (
    FEMALE_VOICES,
    MALE_VOICES,
    InputAudioHistory,
    SpeakerRegistry,
    longest_speech_run,
)

A = [1.0, 0.0, 0.0]
A2 = [0.9, 0.1, 0.0]
B = [0.0, 1.0, 0.0]


def test_same_voice_keeps_identity_and_new_voice_creates_one() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    first = reg.identify(A, 3.0, 120.0)
    assert reg.identify(A2, 3.0, 120.0) is first
    second = reg.identify(B, 3.0, 220.0)
    assert second.ident == 1
    assert reg.identify(A, 3.0, 120.0) is first


def test_short_segment_keeps_current_identity() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    first = reg.identify(A, 3.0, 120.0)
    assert reg.identify(B, 0.8, 220.0) is first
    assert len(reg.speakers) == 1


def test_provisional_identity_adopts_first_reliable_embedding() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    first = reg.identify(None, 0.3, None)
    assert first.count == 0
    assert reg.identify(A, 3.0, 120.0) is first
    assert first.centroid == A and first.count == 1
    assert reg.identify(A2, 3.0, 120.0) is first


def test_voice_follows_pitch_and_is_not_reused_while_free() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    low = reg.identify(A, 3.0, 110.0)
    high = reg.identify(B, 3.0, 230.0)
    other_low = reg.identify([0.0, 0.0, 1.0], 3.0, 100.0)
    assert low.voice == MALE_VOICES[0]
    assert high.voice == FEMALE_VOICES[0]
    assert other_low.voice == MALE_VOICES[1]


def test_input_history_slices_by_absolute_time_after_trimming() -> None:
    hist = InputAudioHistory(sample_rate_hz=10, keep_s=2.0)
    hist.append(np.arange(30, dtype=np.float32))  # 3 s, seules les 2 dernières gardées
    assert hist.slice(1.0, 2.0).tolist() == list(range(10, 20))
    assert hist.slice(0.0, 1.0).tolist() == []


def test_longest_speech_run_drops_the_other_side_of_a_turn_gap() -> None:
    sr = 1000
    gap = np.zeros(int(0.6 * sr), dtype=np.float32)
    audio = np.concatenate([np.full(800, 0.5), gap, np.full(2000, 0.5)]).astype(np.float32)
    assert len(longest_speech_run(audio, sr)) == 2000


def test_longest_speech_run_keeps_audio_with_only_short_pauses() -> None:
    sr = 1000
    audio = np.concatenate([np.full(800, 0.5), np.zeros(200), np.full(800, 0.5)]).astype(np.float32)
    assert len(longest_speech_run(audio, sr)) == len(audio)


def test_short_unmatched_segment_does_not_create_a_speaker() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    first = reg.identify(A, 3.0, 120.0)
    assert reg.identify(B, 1.8, 220.0) is first
    assert len(reg.speakers) == 1
    assert reg.identify(B, 3.0, 220.0).ident == 1


def test_voice_is_reconsidered_once_on_median_pitch() -> None:
    reg = SpeakerRegistry(threshold=0.5)
    speaker = reg.identify(A, 3.0, 140.0)  # première mesure basse : voix grave
    assert speaker.voice in MALE_VOICES
    reg.identify(A2, 3.0, 200.0)
    reg.identify(A, 3.0, 190.0)  # médiane des 3 : 190 Hz
    assert speaker.voice == FEMALE_VOICES[0]
    reg.identify(A, 3.0, 100.0)
    assert speaker.voice == FEMALE_VOICES[0]
