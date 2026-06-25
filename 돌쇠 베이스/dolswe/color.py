import cv2
import numpy as np
from dolswe import config

def dominant_color_name(bgr_region):
    if bgr_region is None or bgr_region.size == 0:
        return None
    hsv = cv2.cvtColor(bgr_region, cv2.COLOR_BGR2HSV)
    h = int(np.median(hsv[:, :, 0]))
    s = int(np.median(hsv[:, :, 1]))
    v = int(np.median(hsv[:, :, 2]))
    if v < config.BLACK_V:
        return "검은"
    if s < config.SAT_GRAY:                      # 채도 낮음 = 무채색 계열
        return "흰" if v > config.WHITE_V else config.GRAY_NAME
    if v < config.CHROMA_V_MIN or s < config.CHROMA_S_MIN:
        # 너무 어둡거나 채도 약함 → 색조(hue) 신뢰 불가. 어두우면 검은, 아니면 무채색.
        return "검은" if v < config.WHITE_V * 0.6 else config.GRAY_NAME
    for h_low, h_high, s_min, v_min, name in config.COLOR_TABLE:
        if h_low <= h <= h_high and s >= s_min and v >= v_min:
            return name
    return config.GRAY_NAME
