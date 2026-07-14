from __future__ import annotations

import numpy as np

from loom_orchestrator.audio_mixer import AudioMixer, IdentityAudioBuffer


def test_identity_buffer_pull_returns_pushed_samples() -> None:
    buf = IdentityAudioBuffer(sample_rate_hz=16000)
    buf.push(np.array([0.1, 0.2, 0.3], dtype=np.float32))
    assert np.allclose(buf.pull(3), [0.1, 0.2, 0.3])


def test_identity_buffer_pull_pads_with_silence_when_short() -> None:
    buf = IdentityAudioBuffer(sample_rate_hz=16000)
    buf.push(np.array([0.5, 0.5], dtype=np.float32))
    assert np.allclose(buf.pull(5), [0.5, 0.5, 0.0, 0.0, 0.0])


def test_identity_buffer_carries_leftover_across_pulls() -> None:
    # Les chunks TTS ne s'alignent jamais sur la taille de bloc du callback audio — deux
    # push() de 2 échantillons chacun, puis des pull(3) qui ne tombent pas sur la frontière.
    buf = IdentityAudioBuffer(sample_rate_hz=16000)
    buf.push(np.array([1.0, 2.0], dtype=np.float32))
    buf.push(np.array([3.0, 4.0], dtype=np.float32))
    assert np.allclose(buf.pull(3), [1.0, 2.0, 3.0])
    assert np.allclose(buf.pull(3), [4.0, 0.0, 0.0])


def test_identity_buffer_drops_oldest_on_overflow() -> None:
    buf = IdentityAudioBuffer(sample_rate_hz=10, max_buffer_s=0.5)  # 5 échantillons max
    buf.push(np.zeros(10, dtype=np.float32))
    assert buf.dropped_samples == 5


def test_mixer_pull_mixed_returns_silence_with_no_identities() -> None:
    mixer = AudioMixer(sample_rate_hz=16000)
    assert np.allclose(mixer.pull_mixed(4), [0.0, 0.0, 0.0, 0.0])


def test_mixer_pull_mixed_passes_single_identity_through_unattenuated() -> None:
    mixer = AudioMixer(sample_rate_hz=16000)
    mixer.push(1, np.array([0.3, -0.3], dtype=np.float32))
    assert np.allclose(mixer.pull_mixed(2), [0.3, -0.3])


def test_mixer_pull_mixed_sums_and_clips_multiple_identities() -> None:
    mixer = AudioMixer(sample_rate_hz=16000)
    mixer.push(1, np.array([0.7, 0.7], dtype=np.float32))
    mixer.push(2, np.array([0.7, -0.7], dtype=np.float32))
    assert np.allclose(mixer.pull_mixed(2), [1.0, 0.0])  # 1.4 -> clippé à 1.0 ; 0.0 inchangé


def test_mixer_silent_identity_contributes_nothing() -> None:
    # Identité enregistrée (ensure_identity) mais jamais alimentée — ne doit rien ajouter au
    # mixage, distinct d'une identité jamais enregistrée du tout.
    mixer = AudioMixer(sample_rate_hz=16000)
    mixer.ensure_identity(1)
    mixer.push(2, np.array([0.4, 0.4], dtype=np.float32))
    assert np.allclose(mixer.pull_mixed(2), [0.4, 0.4])
