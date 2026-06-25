import numpy as np
from dolswe.audio_utils import amplitude_envelope

def test_silence_is_zero():
    env = amplitude_envelope(np.zeros(1000, dtype=np.int16), n_bins=10)
    assert len(env) == 10
    assert all(v == 0.0 for v in env)

def test_full_scale_near_one():
    samples = np.full(1000, 32767, dtype=np.int16)
    env = amplitude_envelope(samples, n_bins=10)
    assert all(0.9 <= v <= 1.0 for v in env)

def test_bins_in_range():
    rng = np.random.RandomState(0)
    samples = (rng.randn(5000) * 5000).astype(np.int16)
    env = amplitude_envelope(samples, n_bins=20)
    assert len(env) == 20
    assert all(0.0 <= v <= 1.0 for v in env)
