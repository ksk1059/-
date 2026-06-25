import numpy as np

def amplitude_envelope(samples, n_bins=20):
    raw = np.asarray(samples)
    arr = raw.astype(np.float64)
    if arr.size == 0:
        return [0.0] * n_bins
    if np.issubdtype(raw.dtype, np.integer):
        arr = arr / 32768.0
    bins = np.array_split(arr, n_bins)
    out = []
    for b in bins:
        rms = float(np.sqrt(np.mean(b ** 2))) if b.size else 0.0
        out.append(min(1.0, rms))
    return out
