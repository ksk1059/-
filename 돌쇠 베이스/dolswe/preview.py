# dolswe/preview.py
# 메인 UI에 통합되는 카메라 프리뷰 렌더. 라이브 프레임에 YOLO 박스 + 손 랜드마크 +
# 한글 정보패널을 그려 JPEG(base64)로 반환. (OpenCV는 한글 못 그려 PIL 사용)
import base64
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

_FONT = ImageFont.truetype(r"C:\Windows\Fonts\malgun.ttf", 18)
_W = 480  # 프리뷰 폭 (대역폭 절약)
# MediaPipe Hands 21점 연결(뼈대)
_HAND_CONN = [(0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8),
              (5, 9), (9, 10), (10, 11), (11, 12), (9, 13), (13, 14), (14, 15),
              (15, 16), (13, 17), (17, 18), (18, 19), (19, 20), (0, 17)]


def render(frame, boxes, hand_pts, info_lines):
    h, w = frame.shape[:2]
    scale = _W / w
    img = cv2.resize(frame, (_W, int(h * scale)))
    H = img.shape[0]
    # 박스 (cv2 사각형; 라벨은 아래 PIL에서)
    for x1, y1, x2, y2, label, is_person in boxes:
        col = (0, 180, 255) if is_person else (80, 220, 80)
        cv2.rectangle(img, (int(x1 * scale), int(y1 * scale)),
                      (int(x2 * scale), int(y2 * scale)), col, 2)
    # 손 랜드마크 (뼈대 선 + 점) — 양손 모두
    for hand in (hand_pts or []):
        px = [(int(x * _W), int(y * H)) for x, y in hand]
        for a, b in _HAND_CONN:
            if a < len(px) and b < len(px):
                cv2.line(img, px[a], px[b], (255, 0, 255), 2)
        for p in px:
            cv2.circle(img, p, 4, (0, 255, 255), -1)
    # PIL: 한글 박스 라벨 + 상단 정보패널
    pil = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    d = ImageDraw.Draw(pil)
    for x1, y1, x2, y2, label, is_person in boxes:
        col = (0, 180, 255) if is_person else (80, 220, 80)
        d.text((int(x1 * scale) + 2, max(0, int(y1 * scale) - 18)), label,
               font=_FONT, fill=col)
    if info_lines:
        d.rectangle([0, 0, _W, 6 + 22 * len(info_lines)], fill=(0, 0, 0))
        for i, t in enumerate(info_lines):
            d.text((6, 3 + 22 * i), t, font=_FONT, fill=(255, 255, 255))
    out = cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)
    ok, buf = cv2.imencode(".jpg", out, [cv2.IMWRITE_JPEG_QUALITY, 55])
    if not ok:
        return None
    return base64.b64encode(buf).decode("ascii")
