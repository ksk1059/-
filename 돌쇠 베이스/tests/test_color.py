import numpy as np
from dolswe.color import dominant_color_name

def _solid(b, g, r, h=20, w=20):
    img = np.zeros((h, w, 3), dtype=np.uint8)
    img[:, :] = (b, g, r)
    return img

def test_red():
    assert dominant_color_name(_solid(0, 0, 255)) == "빨간"

def test_blue():
    assert dominant_color_name(_solid(255, 0, 0)) == "파란"

def test_green():
    assert dominant_color_name(_solid(0, 255, 0)) == "초록"

def test_white_is_achromatic():
    assert dominant_color_name(_solid(255, 255, 255)) in ("무채색", "흰")

def test_empty_returns_none():
    assert dominant_color_name(np.zeros((0, 0, 3), dtype=np.uint8)) is None
