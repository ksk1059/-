# -*- coding: utf-8 -*-
"""
면상 판독기 (셀프 키오스크)
------------------------------------------------------------
행사장에서 참가자가 직접 사용. 모든 조작은 화면의 파란 버튼을 마우스/터치.
진행요원은 키보드로 ADMIN_EXIT_CODE 를 입력해 종료.

흐름:
  [SETUP]  인원수 설정  →  [CAPTURE] 촬영(3·2·1 카운트다운+플래시)
  →  [RESULT] 개인 닮음도  →  (인원수만큼 반복)  →  [FINAL] 우승 발표
  →  [새 게임] 누르면 다시 SETUP (프로그램 재실행 불필요)

얼굴 검출 : OpenCV YuNet
얼굴 임베딩: OpenCV SFace (코사인 유사도)
"""

import os
import sys
import shutil
import tempfile
import numpy as np
import cv2
from PIL import Image, ImageDraw, ImageFont

# ======================================================================
#  ★★★  설정 — 여기 값만 바꾸면 됩니다  ★★★
#  색상은 (R, G, B), 0~255.  크기는 픽셀.  시간은 밀리초(ms).
# ======================================================================
WINDOW_NAME      = "면상 판독기"
FONT_PATH        = r"C:\Windows\Fonts\malgun.ttf"   # 한글 폰트 경로

# --- 화면(캔버스) 크기 / 사이드바 --------------------------------------
CANVAS_W, CANVAS_H = 1280, 720      # 내부 작업 해상도(전체화면으로 확대됨)
SIDEBAR_W          = 320            # 오른쪽 버튼 사이드바 폭
FULLSCREEN         = True           # True=전체화면, False=창모드
MIRROR             = True           # 카메라 좌우반전(셀카처럼)

# --- 관리자 종료 -------------------------------------------------------
ADMIN_EXIT_CODE  = "quit"   # 키보드로 이 글자를 차례로 입력하면 종료(참가자 오종료 방지)

# --- 인원수 ------------------------------------------------------------
DEFAULT_PEOPLE   = 1
MAX_PEOPLE       = 99

# --- 닮음도 과장 보정 --------------------------------------------------
EXAGGERATE_FULL  = 70.0     # 이 코사인 유사도(%)를 100%로 표시(작을수록 더 과장)

# --- 카운트다운 / 플래시 -----------------------------------------------
COUNTDOWN_NUMS   = (3, 2, 1)
COUNTDOWN_STEP_MS = 700     # 숫자 하나당 지속 시간(ms)
FLASH_STEPS      = (1.0, 0.6, 0.3)
FLASH_DELAY_MS   = 40

# --- 경고 표시 시간 ----------------------------------------------------
WARN_WAIT_MS     = 1500     # "얼굴 못 찾음" 표시 시간

# --- 글자 크기(px) -----------------------------------------------------
SIZE_TITLE       = 34
SIZE_BODY        = 28
SIZE_HINT        = 20
SIZE_BUTTON      = 30
SIZE_BIG         = 200      # 카운트다운 큰 숫자

# --- 색상 (R, G, B) ----------------------------------------------------
COLOR_BG         = (255, 255, 255)   # 사이드바 배경(흰색)
COLOR_SEP        = (220, 224, 230)   # 카메라/사이드바 구분선
COLOR_BTN        = (0, 74, 255)      # 버튼(파란색 #004AFF)
COLOR_BTN_DOWN   = (0, 60, 210)      # 버튼 눌림
COLOR_BTN_TEXT   = (255, 255, 255)   # 버튼 글자(흰색)
COLOR_SBTITLE    = (30, 35, 45)      # 사이드바 제목(짙은 회색)
COLOR_SBINFO     = (90, 100, 115)    # 사이드바 상태
COLOR_NUM        = (0, 74, 255)      # 인원수 숫자

# --- 로고(오른쪽 아래) -------------------------------------------------
LOGO_FILE        = "logo.png"   # Profile.svg 를 구운 PNG (없으면 표시 생략)
LOGO_SIZE        = 72           # 로고 한 변 크기(px)
LOGO_MARGIN      = 18           # 화면 가장자리 여백(px)

# 카메라 위 오버레이 글자
COLOR_TITLE      = (255, 230, 120)   # 결과 제목(노랑)
COLOR_NAME       = (255, 255, 255)   # 사진 이름(흰색)
COLOR_SCORE      = (120, 255, 160)   # 닮음도(연두)
COLOR_WARN       = (255, 120, 120)   # 경고(빨강)
COLOR_WINNER     = (255, 215, 0)     # 최종 우승(금색)
COLOR_BIG        = (255, 255, 255)   # 카운트다운 숫자
OVERLAY_ALPHA    = 0.55              # 카메라 위 검은 배너 진하기

# --- 기타 --------------------------------------------------------------
DETECT_SCORE_THRESHOLD = 0.7   # 얼굴 검출 민감도(낮출수록 잘 잡지만 오검출↑)
# ======================================================================

CAM_W = CANVAS_W - SIDEBAR_W   # 카메라 영역 폭
CAM_H = CANVAS_H
SB_X  = CAM_W                  # 사이드바 시작 x
SB_MARGIN = 20
BTN_X = SB_X + SB_MARGIN
BTN_W = SIDEBAR_W - SB_MARGIN * 2

# --- 전체화면 해상도 감지(텍스트 계단현상 방지: 네이티브 해상도로 출력) ----
def _detect_screen():
    try:
        import ctypes
        ctypes.windll.user32.SetProcessDPIAware()
        u = ctypes.windll.user32
        return int(u.GetSystemMetrics(0)), int(u.GetSystemMetrics(1))
    except Exception:
        return CANVAS_W, CANVAS_H

SCREEN_W, SCREEN_H = _detect_screen() if FULLSCREEN else (CANVAS_W, CANVAS_H)
_UPSCALE = FULLSCREEN and (SCREEN_W, SCREEN_H) != (CANVAS_W, CANVAS_H)

# ----------------------------------------------------------------------
# 경로 설정
# ----------------------------------------------------------------------
BASE_DIR    = os.path.dirname(os.path.abspath(__file__))
COMPARE_DIR = os.path.join(BASE_DIR, "비교군")
TEMP_DIR    = os.path.join(BASE_DIR, "임시저장")
MODEL_DIR   = os.path.join(BASE_DIR, "models")

YUNET_PATH  = os.path.join(MODEL_DIR, "face_detection_yunet_2023mar.onnx")
SFACE_PATH  = os.path.join(MODEL_DIR, "face_recognition_sface_2021dec.onnx")

IMG_EXTS    = (".jpg", ".jpeg", ".png", ".bmp", ".webp")

os.makedirs(COMPARE_DIR, exist_ok=True)
os.makedirs(TEMP_DIR, exist_ok=True)

# ----------------------------------------------------------------------
# 한글 경로 대응 입출력
# ----------------------------------------------------------------------
def imread_kr(path):
    try:
        data = np.fromfile(path, dtype=np.uint8)
        return cv2.imdecode(data, cv2.IMREAD_COLOR)
    except Exception:
        return None

def imwrite_kr(path, img):
    ext = os.path.splitext(path)[1]
    ok, buf = cv2.imencode(ext, img)
    if ok:
        buf.tofile(path)
    return ok

# ----------------------------------------------------------------------
# 한글 텍스트 그리기 (한 프레임의 모든 글자를 PIL 1회 변환으로 처리)
# ----------------------------------------------------------------------
_font_cache = {}
def _get_font(size):
    if size not in _font_cache:
        try:
            _font_cache[size] = ImageFont.truetype(FONT_PATH, size)
        except Exception:
            _font_cache[size] = ImageFont.load_default()
    return _font_cache[size]

def put_texts_kr(img, items):
    """items: [{'text','pos','size','color', 'anchor'?, 'stroke'?, 'stroke_color'?}, ...]
    color/stroke_color 는 RGB. anchor 기본 'la'(좌상단), 가운데정렬은 'mm'."""
    if not items:
        return img
    pil = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    draw = ImageDraw.Draw(pil)
    for it in items:
        draw.text(
            it["pos"], it["text"], font=_get_font(it.get("size", SIZE_BODY)),
            fill=it.get("color", (255, 255, 255)),
            anchor=it.get("anchor", "la"),
            stroke_width=it.get("stroke", 0),
            stroke_fill=it.get("stroke_color", (0, 0, 0)),
        )
    return cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)

# ----------------------------------------------------------------------
# 버튼
# ----------------------------------------------------------------------
class Button:
    def __init__(self, bid, x, y, w, h, label, size=SIZE_BUTTON):
        self.id, self.label, self.size = bid, label, size
        self.x, self.y, self.w, self.h = x, y, w, h

    def hit(self, mx, my):
        return self.x <= mx <= self.x + self.w and self.y <= my <= self.y + self.h

def _rgb2bgr(c):
    return (c[2], c[1], c[0])

def draw_round_rect(img, x, y, w, h, color_rgb, radius=18):
    c = _rgb2bgr(color_rgb)
    radius = min(radius, w // 2, h // 2)
    cv2.rectangle(img, (x + radius, y), (x + w - radius, y + h), c, -1)
    cv2.rectangle(img, (x, y + radius), (x + w, y + h - radius), c, -1)
    for cx, cy in ((x + radius, y + radius), (x + w - radius, y + radius),
                   (x + radius, y + h - radius), (x + w - radius, y + h - radius)):
        cv2.circle(img, (cx, cy), radius, c, -1, lineType=cv2.LINE_AA)

def draw_buttons(canvas, buttons, pressed_id=None):
    """버튼 사각형(cv2)만 그림. 글자 항목은 리스트로 반환해 한 번에 그리도록."""
    text_items = []
    for b in buttons:
        color = COLOR_BTN_DOWN if b.id == pressed_id else COLOR_BTN
        draw_round_rect(canvas, b.x, b.y, b.w, b.h, color)
        text_items.append({
            "text": b.label, "pos": (b.x + b.w // 2, b.y + b.h // 2),
            "size": b.size, "color": COLOR_BTN_TEXT, "anchor": "mm",
        })
    return text_items

# ----------------------------------------------------------------------
# 모델 로드
# ----------------------------------------------------------------------
def _ascii_path(src):
    """OpenCV 모델 로더는 비ASCII 경로를 못 읽으므로 임시폴더(ASCII)로 복사."""
    if src.isascii():
        return src
    dst = os.path.join(tempfile.gettempdir(), os.path.basename(src))
    if (not os.path.exists(dst)) or os.path.getsize(dst) != os.path.getsize(src):
        shutil.copyfile(src, dst)
    return dst

def load_models():
    if not (os.path.exists(YUNET_PATH) and os.path.exists(SFACE_PATH)):
        print("[오류] models 폴더에 ONNX 모델이 없습니다.")
        sys.exit(1)
    detector   = cv2.FaceDetectorYN.create(_ascii_path(YUNET_PATH), "", (320, 320),
                                           score_threshold=DETECT_SCORE_THRESHOLD)
    recognizer = cv2.FaceRecognizerSF.create(_ascii_path(SFACE_PATH), "")
    return detector, recognizer

# ----------------------------------------------------------------------
# 얼굴 검출 / 임베딩 / 매칭
# ----------------------------------------------------------------------
def detect_largest_face(detector, img):
    h, w = img.shape[:2]
    detector.setInputSize((w, h))
    _, faces = detector.detect(img)
    if faces is None or len(faces) == 0:
        return None
    faces = sorted(faces, key=lambda f: f[2] * f[3], reverse=True)
    return faces[0]

def get_feature(detector, recognizer, img):
    face = detect_largest_face(detector, img)
    if face is None:
        return None
    aligned = recognizer.alignCrop(img, face)
    return recognizer.feature(aligned)

def build_compare_db(detector, recognizer):
    db = []
    files = [f for f in os.listdir(COMPARE_DIR) if f.lower().endswith(IMG_EXTS)]
    print(f"\n[비교군] '{COMPARE_DIR}' 에서 {len(files)}개 이미지 로딩 중...")
    for fn in files:
        img = imread_kr(os.path.join(COMPARE_DIR, fn))
        if img is None:
            print(f"  - {fn}: 읽기 실패(건너뜀)")
            continue
        feat = get_feature(detector, recognizer, img)
        if feat is None:
            print(f"  - {fn}: 얼굴 검출 실패(건너뜀)")
            continue
        name = os.path.splitext(fn)[0]
        db.append((name, feat))
        print(f"  + {name}: 등록 완료")
    return db

def best_match(recognizer, feat, db):
    best_name, best_cos = None, -1.0
    for name, ref in db:
        cos = recognizer.match(feat, ref, cv2.FaceRecognizerSF_FR_COSINE)
        if cos > best_cos:
            best_cos, best_name = cos, name
    if best_name is None:
        return None
    raw = max(0.0, min(1.0, best_cos)) * 100.0
    percent = min(100.0, raw / EXAGGERATE_FULL * 100.0)
    return best_name, percent

# ----------------------------------------------------------------------
# 화면 합성
# ----------------------------------------------------------------------
_logo_cache = None   # None=미로드, False=없음, ndarray=(bgr, alpha)
def _get_logo():
    global _logo_cache
    if _logo_cache is None:
        path = os.path.join(BASE_DIR, LOGO_FILE)
        raw = None
        if os.path.exists(path):
            try:
                data = np.fromfile(path, dtype=np.uint8)
                raw = cv2.imdecode(data, cv2.IMREAD_UNCHANGED)
            except Exception:
                raw = None
        if raw is None:
            _logo_cache = False
        else:
            raw = cv2.resize(raw, (LOGO_SIZE, LOGO_SIZE))
            if raw.shape[2] == 4:
                bgr = raw[:, :, :3]
                alpha = raw[:, :, 3:4].astype(np.float32) / 255.0
            else:
                bgr = raw[:, :, :3]
                alpha = np.ones((LOGO_SIZE, LOGO_SIZE, 1), np.float32)
            _logo_cache = (bgr, alpha)
    return _logo_cache

def overlay_logo(canvas):
    logo = _get_logo()
    if not logo:
        return
    bgr, alpha = logo
    x2, y2 = CANVAS_W - LOGO_MARGIN, CANVAS_H - LOGO_MARGIN
    x1, y1 = x2 - LOGO_SIZE, y2 - LOGO_SIZE
    roi = canvas[y1:y2, x1:x2].astype(np.float32)
    blended = bgr.astype(np.float32) * alpha + roi * (1 - alpha)
    canvas[y1:y2, x1:x2] = blended.astype(np.uint8)

def make_canvas(cam_frame, flash_alpha=0.0):
    """흰 캔버스 위에 카메라 영역(좌) + 빈 사이드바(우, 흰색) 배치."""
    canvas = np.full((CANVAS_H, CANVAS_W, 3), _rgb2bgr(COLOR_BG), dtype=np.uint8)
    cam = cv2.resize(cam_frame, (CAM_W, CAM_H))
    if flash_alpha > 0:
        white = np.full_like(cam, 255)
        cam = cv2.addWeighted(white, flash_alpha, cam, 1 - flash_alpha, 0)
    canvas[0:CAM_H, 0:CAM_W] = cam
    cv2.line(canvas, (SB_X, 0), (SB_X, CANVAS_H), _rgb2bgr(COLOR_SEP), 2)
    overlay_logo(canvas)
    return canvas

def cam_banner(canvas, height):
    """카메라 영역 상단에 반투명 검은 배너(글자 잘 보이게)."""
    region = canvas[:height, :CAM_W].copy()
    black = np.zeros_like(region)
    blended = cv2.addWeighted(black, OVERLAY_ALPHA, region, 1 - OVERLAY_ALPHA, 0)
    canvas[:height, :CAM_W] = blended

def sidebar_header(title, status):
    """사이드바 상단 제목/상태 텍스트 항목 반환."""
    items = [{"text": title, "pos": (BTN_X, 28), "size": SIZE_TITLE,
              "color": COLOR_SBTITLE}]
    if status:
        items.append({"text": status, "pos": (BTN_X, 78), "size": SIZE_HINT,
                      "color": COLOR_SBINFO})
    return items

# ----------------------------------------------------------------------
# 버튼 레이아웃(상태별)
# ----------------------------------------------------------------------
def layout_buttons(state, total=1):
    if state == "SETUP":
        sq = 80
        minus = Button("minus", BTN_X, 260, sq, sq, "-", size=44)
        plus  = Button("plus", SB_X + SIDEBAR_W - SB_MARGIN - sq, 260, sq, sq, "+", size=44)
        start = Button("start", BTN_X, 420, BTN_W, 100, "시작", size=SIZE_BUTTON)
        return [minus, plus, start]
    if state == "CAPTURE":
        return [Button("shoot", BTN_X, 480, BTN_W, 130, "촬영", size=40)]
    if state == "RESULT":
        return [Button("next", BTN_X, 450, BTN_W, 100, "다음", size=SIZE_BUTTON)]
    if state == "FINAL":
        return [Button("newgame", BTN_X, 450, BTN_W, 100, "새 게임", size=SIZE_BUTTON)]
    return []

# ----------------------------------------------------------------------
# 마우스 클릭 상태(전역)
# ----------------------------------------------------------------------
_mouse = {"clicked": False, "x": 0, "y": 0, "down": False}

def _on_mouse(event, x, y, flags, param):
    if event == cv2.EVENT_LBUTTONDOWN:
        _mouse["down"] = True
        _mouse["x"], _mouse["y"] = x, y
    elif event == cv2.EVENT_LBUTTONUP:
        _mouse["down"] = False
        _mouse["clicked"] = True
        _mouse["x"], _mouse["y"] = x, y

def _map_xy(x, y):
    """화면(출력) 좌표 → 캔버스 좌표 역매핑."""
    if _UPSCALE:
        return int(x * CANVAS_W / SCREEN_W), int(y * CANVAS_H / SCREEN_H)
    return x, y

def take_click():
    if _mouse["clicked"]:
        _mouse["clicked"] = False
        return _map_xy(_mouse["x"], _mouse["y"])
    return None

def hovered_button(buttons):
    """현재 누르고 있는 버튼 id(눌림 표시용)."""
    if not _mouse["down"]:
        return None
    mx, my = _map_xy(_mouse["x"], _mouse["y"])
    for b in buttons:
        if b.hit(mx, my):
            return b.id
    return None

# ----------------------------------------------------------------------
# 카운트다운 + 촬영
# ----------------------------------------------------------------------
def read_frame(cap):
    ret, frame = cap.read()
    if not ret:
        return None
    if MIRROR:
        frame = cv2.flip(frame, 1)
    return frame

def display(canvas):
    """캔버스를 화면 해상도로 부드럽게 리샘플 후 1:1 출력(확대 계단현상 방지)."""
    if _UPSCALE:
        canvas = cv2.resize(canvas, (SCREEN_W, SCREEN_H), interpolation=cv2.INTER_CUBIC)
    cv2.imshow(WINDOW_NAME, canvas)

def show_canvas(canvas, text_items):
    canvas = put_texts_kr(canvas, text_items)
    display(canvas)

def countdown_and_shoot(cap, idx, total):
    """3·2·1 카운트다운(라이브) → 플래시 → 마지막 프레임 반환. 실패 시 None."""
    for n in COUNTDOWN_NUMS:
        steps = max(1, COUNTDOWN_STEP_MS // 30)
        for _ in range(steps):
            frame = read_frame(cap)
            if frame is None:
                return None
            canvas = make_canvas(frame)
            items = sidebar_header(WINDOW_NAME, f"{idx} / {total} 번째 · 촬영 중")
            items.append({"text": str(n), "pos": (CAM_W // 2, CAM_H // 2),
                          "size": SIZE_BIG, "color": COLOR_BIG, "anchor": "mm",
                          "stroke": 6, "stroke_color": (0, 0, 0)})
            show_canvas(canvas, items)
            cv2.waitKey(30)
    shot = read_frame(cap)
    if shot is None:
        return None
    # 플래시
    for alpha in FLASH_STEPS:
        canvas = make_canvas(shot, flash_alpha=alpha)
        display(canvas)
        cv2.waitKey(FLASH_DELAY_MS)
    return shot

# ----------------------------------------------------------------------
# 메인 (상태머신)
# ----------------------------------------------------------------------
def main():
    print("=" * 50)
    print("            면 상 판 독 기  (셀프 키오스크)")
    print("=" * 50)

    detector, recognizer = load_models()
    db = build_compare_db(detector, recognizer)
    if not db:
        print("\n[경고] '비교군' 폴더에 얼굴 사진이 없습니다. 사진을 넣고 다시 실행하세요.")
        return

    cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
    if not cap.isOpened():
        cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        print("[오류] 카메라를 열 수 없습니다.")
        return

    cv2.namedWindow(WINDOW_NAME, cv2.WINDOW_NORMAL)
    if FULLSCREEN:
        cv2.setWindowProperty(WINDOW_NAME, cv2.WND_PROP_FULLSCREEN, cv2.WINDOW_FULLSCREEN)
    cv2.setMouseCallback(WINDOW_NAME, _on_mouse)

    print(f"\n진행요원 종료: 키보드로 '{ADMIN_EXIT_CODE}' 입력")

    state = "SETUP"
    total = DEFAULT_PEOPLE
    idx = 1
    results = []                 # [(idx, name, percent, path), ...]
    cur = None                   # (idx, name, percent, shot)
    winner_img = None
    key_buf = ""

    while True:
        # 창이 닫혔으면 종료
        if cv2.getWindowProperty(WINDOW_NAME, cv2.WND_PROP_VISIBLE) < 1:
            break

        # ---- 정지화면 상태(RESULT/FINAL)는 저장된 이미지, 그 외는 라이브 ----
        if state == "RESULT":
            frame = cur[3]
        elif state == "FINAL":
            frame = winner_img
        else:
            frame = read_frame(cap)
            if frame is None:
                print("[오류] 카메라 프레임을 읽지 못했습니다.")
                break

        buttons = layout_buttons(state, total)
        canvas = make_canvas(frame)
        pressed = hovered_button(buttons)
        btn_text = draw_buttons(canvas, buttons, pressed)
        items = []

        # ---- 상태별 화면 구성 ----
        if state == "SETUP":
            items += sidebar_header(WINDOW_NAME, "인원수를 정하고 [시작]")
            items.append({"text": "인원수", "pos": (BTN_X + BTN_W // 2, 180),
                          "size": SIZE_BODY, "color": COLOR_SBINFO, "anchor": "mm"})
            items.append({"text": f"{total} 명", "pos": (BTN_X + BTN_W // 2, 300),
                          "size": 52, "color": COLOR_NUM, "anchor": "mm"})
            cam_banner(canvas, 70)
            items.append({"text": "참가자 셀프 닮은꼴 판독기", "pos": (20, 18),
                          "size": SIZE_BODY, "color": COLOR_NAME, "stroke": 2})

        elif state == "CAPTURE":
            items += sidebar_header(WINDOW_NAME, f"{idx} / {total} 번째")
            cam_banner(canvas, 70)
            items.append({"text": f"{idx} / {total} · [촬영] 버튼을 누르세요",
                          "pos": (20, 18), "size": SIZE_BODY, "color": COLOR_NAME,
                          "stroke": 2})

        elif state == "RESULT":
            _, name, percent, _ = cur
            items += sidebar_header(WINDOW_NAME, "다음 사람은 [다음]")
            cam_banner(canvas, 135)
            items.append({"text": f"[{cur[0]}/{total}] 닮은꼴 판독 결과",
                          "pos": (20, 14), "size": SIZE_TITLE, "color": COLOR_TITLE,
                          "stroke": 2})
            items.append({"text": f"가장 닮은 사진 : {name}", "pos": (20, 58),
                          "size": SIZE_BODY, "color": COLOR_NAME, "stroke": 2})
            items.append({"text": f"닮음도 : {percent:.1f}%", "pos": (20, 95),
                          "size": SIZE_BODY, "color": COLOR_SCORE, "stroke": 2})

        elif state == "FINAL":
            w_idx, w_name, w_pct, _ = winner
            items += sidebar_header(WINDOW_NAME, "[새 게임]으로 다시 시작")
            cam_banner(canvas, 135)
            items.append({"text": "★ 최종 우승 (가장 닮은 사람) ★", "pos": (20, 12),
                          "size": SIZE_TITLE, "color": COLOR_WINNER, "stroke": 2})
            items.append({"text": f"{w_idx}번째 - {w_name}", "pos": (20, 58),
                          "size": SIZE_BODY, "color": COLOR_NAME, "stroke": 2})
            items.append({"text": f"닮음도 : {w_pct:.1f}%", "pos": (20, 95),
                          "size": SIZE_BODY, "color": COLOR_SCORE, "stroke": 2})

        show_canvas(canvas, items + btn_text)

        # ---- 입력 처리 ----
        key = cv2.waitKey(1) & 0xFF
        if key != 255 and 32 <= key <= 126:
            key_buf = (key_buf + chr(key).lower())[-len(ADMIN_EXIT_CODE):]
            if key_buf == ADMIN_EXIT_CODE.lower():
                print("\n[관리자] 종료 명령 입력됨.")
                break

        click = take_click()
        if click is None:
            continue
        cx, cy = click
        hit = next((b for b in buttons if b.hit(cx, cy)), None)
        if hit is None:
            continue

        # ---- 버튼 동작 ----
        if hit.id == "minus":
            total = max(1, total - 1)
        elif hit.id == "plus":
            total = min(MAX_PEOPLE, total + 1)
        elif hit.id == "start":
            idx, results = 1, []
            state = "CAPTURE"
        elif hit.id == "shoot":
            shot = countdown_and_shoot(cap, idx, total)
            if shot is None:
                break
            feat = get_feature(detector, recognizer, shot)
            if feat is None:
                # 얼굴 미검출 → 경고 후 같은 번호 재촬영
                canvas = make_canvas(shot)
                cam_banner(canvas, 70)
                warn = [{"text": "얼굴을 찾지 못했어요. 다시 촬영하세요.",
                         "pos": (20, 18), "size": SIZE_TITLE, "color": COLOR_WARN,
                         "stroke": 2}]
                show_canvas(canvas, warn)
                cv2.waitKey(WARN_WAIT_MS)
                continue
            save_path = os.path.join(TEMP_DIR, f"person_{idx:02d}.jpg")
            imwrite_kr(save_path, shot)
            name, percent = best_match(recognizer, feat, db)
            print(f"  {idx}번째 → {name} (닮음도 {percent:.1f}%)  저장: {save_path}")
            cur = (idx, name, percent, shot)
            state = "RESULT"
        elif hit.id == "next":
            results.append((cur[0], cur[1], cur[2], os.path.join(TEMP_DIR, f"person_{cur[0]:02d}.jpg")))
            idx += 1
            if idx > total:
                winner = max(results, key=lambda r: r[2])
                w_path = winner[3]
                winner_img = imread_kr(w_path)
                if winner_img is None:
                    winner_img = cur[3]
                print("\n" + "=" * 50)
                print("                 최 종 결 과")
                for i_, n_, p_, _ in results:
                    mark = "  <== 최고!" if i_ == winner[0] else ""
                    print(f"  {i_}번째 : {n_} ({p_:.1f}%){mark}")
                print(f"  우승: {winner[0]}번째 / {winner[1]} / {winner[2]:.1f}%")
                print("=" * 50)
                state = "FINAL"
            else:
                state = "CAPTURE"
        elif hit.id == "newgame":
            state = "SETUP"
            idx, total, results = 1, DEFAULT_PEOPLE, []

    cap.release()
    cv2.destroyAllWindows()
    print("\n프로그램을 종료합니다.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        cv2.destroyAllWindows()
        print("\n사용자에 의해 중단되었습니다.")
