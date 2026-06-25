# dolswe/camera_test.py
# 카메라 진단 도구. 실행: python -m dolswe.camera_test
# - 0~3번 인덱스 스캔해 열리는 카메라 찾음
# - 실시간 영상 위에 YOLO-World 박스/인원/옷색/장면 표시
# - 창 위 텍스트로 ctx 값 확인. q 또는 ESC로 종료.
import os, time
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from ultralytics import YOLO
from dolswe import config
from dolswe.color import dominant_color_name

_FONT = ImageFont.truetype(r"C:\Windows\Fonts\malgun.ttf", 26)  # 본문


def draw_panel(frame, lines):
    # OpenCV putText는 한글 못 그림(????) → PIL 맑은고딕. 반투명 패널로 가독성↑
    img = Image.fromarray(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)).convert("RGB")
    overlay = Image.new("RGB", img.size, (0, 0, 0))
    mask = Image.new("L", img.size, 0)
    md = ImageDraw.Draw(mask)
    panel_h = 18 + 34 * len(lines)
    md.rectangle([0, 0, img.size[0], panel_h], fill=160)  # 반투명도
    img = Image.composite(overlay, img, mask)
    d = ImageDraw.Draw(img)
    for i, t in enumerate(lines):
        d.text((10, 8 + 34 * i), t, font=_FONT, fill=(255, 255, 255))
    return cv2.cvtColor(np.array(img), cv2.COLOR_RGB2BGR)

_MODEL_PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           "models", config.WORLD_MODEL)


def open_camera():
    for idx in range(4):
        cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW)  # Windows: DSHOW가 빠름
        if cap.isOpened():
            ok, _ = cap.read()
            if ok:
                print(f"[OK] 카메라 인덱스 {idx} 사용")
                return cap
            cap.release()
        print(f"[--] 인덱스 {idx} 안 열림")
    return None


def main():
    cap = open_camera()
    if cap is None:
        print("[FAIL] 카메라 못 찾음. 다른 앱이 점유 중이거나 장치 없음/권한 문제.")
        return

    print("YOLO-World 로딩...")
    model = YOLO(_MODEL_PATH if os.path.exists(_MODEL_PATH) else config.WORLD_MODEL)
    prompts = [en for en, _ in config.SCENE_ITEMS]
    ko = dict(config.SCENE_ITEMS)
    model.set_classes(prompts)

    fps_t, fps_n, fps = time.time(), 0, 0.0
    YOLO_EVERY = 3  # YOLO는 N프레임마다만 (무거움). 결과 캐시해 매 프레임 그림.
    frame_i = 0
    people, color, labels, draw_boxes = 0, None, [], []

    print("창 떴으면 카메라 정상. q/ESC 종료.")
    while True:
        ok, frame = cap.read()
        if not ok:
            print("[WARN] 프레임 읽기 실패")
            break

        if frame_i % YOLO_EVERY == 0:  # 무거운 YOLO는 가끔, 결과는 캐시해 매 프레임 그림
            res = model(frame, conf=config.SCENE_CONF, verbose=False)[0]
            names = res.names
            people, color, labels, draw_boxes = 0, None, [], []
            seen = set()
            big_area, big_box = -1, None
            if res.boxes is not None:
                for b in res.boxes:
                    c = int(b.cls[0]); cf = float(b.conf[0])
                    x1, y1, x2, y2 = map(int, b.xyxy[0])
                    en = names[c]
                    if en == "person":
                        if cf < config.PERSON_CONF:
                            continue
                        people += 1
                        area = (x2 - x1) * (y2 - y1)
                        if area > big_area:
                            big_area, big_box = area, (x1, y1, x2, y2)
                        box_col = (0, 180, 255)
                    else:
                        lab = ko.get(en)
                        if lab and lab not in seen:
                            seen.add(lab); labels.append(lab)
                        box_col = (80, 220, 80)
                    draw_boxes.append((x1, y1, x2, y2, box_col, f"{en} {cf:.2f}"))
            if big_box:
                x1, y1, x2, y2 = big_box
                ty1 = int(y1 + (y2 - y1) * 0.15); ty2 = int(y1 + (y2 - y1) * 0.50)
                tx1 = int(x1 + (x2 - x1) * 0.30); tx2 = int(x1 + (x2 - x1) * 0.70)
                if ty2 > ty1 and tx2 > tx1:
                    color = dominant_color_name(frame[ty1:ty2, tx1:tx2])
                    draw_boxes.append((tx1, ty1, tx2, ty2, (255, 0, 255), None))
        frame_i += 1

        for x1, y1, x2, y2, col, txt in draw_boxes:  # 캐시된 박스 매 프레임 그림
            cv2.rectangle(frame, (x1, y1), (x2, y2), col, 2)
            if txt:
                cv2.putText(frame, txt, (x1, max(12, y1 - 5)),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.5, col, 1)

        fps_n += 1
        if time.time() - fps_t >= 1.0:
            fps = fps_n / (time.time() - fps_t)
            fps_t, fps_n = time.time(), 0

        line1 = f"사람 {people}명   옷색 {color or '-'}   fps {fps:.0f}"
        line2 = "장면: " + (", ".join(labels) if labels else "-")
        frame = draw_panel(frame, [line1, line2])

        cv2.imshow("dolswe camera test (q/ESC quit)", frame)
        if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
            break

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
