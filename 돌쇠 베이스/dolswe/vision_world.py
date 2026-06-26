# dolswe/vision_world.py
import os, threading, time, collections
import numpy as np
from ultralytics import YOLO
from dolswe import config
from dolswe.color import dominant_color_name

_MODEL_PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           "models", config.WORLD_MODEL)


class VisionWorld:
    """YOLO-World 오픈보캐브 디텍션으로 인원/옷색/장면을 한 번에. 인사 트리거 소유."""

    def __init__(self, ctx, get_frame, on_new_person):
        self._ctx = ctx
        self._get_frame = get_frame
        self._on_new_person = on_new_person
        self._running = False
        self._thread = None
        self._prompts = [en for en, _ in config.SCENE_ITEMS]
        self._ko = dict(config.SCENE_ITEMS)
        self._model = YOLO(_MODEL_PATH if os.path.exists(_MODEL_PATH) else config.WORLD_MODEL)
        self._model.set_classes(self._prompts)
        # 스무딩 + 체류 상태머신
        self._color_hist = collections.deque(maxlen=config.COLOR_SMOOTH_FRAMES)
        self._count_hist = collections.deque(maxlen=config.COUNT_SMOOTH_FRAMES)
        self._present = False
        self._pos_frames = 0
        self._neg_frames = 0
        self._last_greet = -1e9
        self.last_boxes = []  # 프리뷰용: [(x1,y1,x2,y2,label,is_person)]

    def start(self):
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False

    def _loop(self):
        interval = 1.0 / config.WORLD_FPS
        while self._running:
            t0 = time.time()
            frame = self._get_frame()
            if frame is not None:
                raw_count, raw_color, scene, obj_crop = self._infer(frame)
                self._count_hist.append(raw_count)
                count = collections.Counter(self._count_hist).most_common(1)[0][0]
                if count == 0:
                    self._color_hist.clear()
                    color = None
                else:
                    if raw_color:
                        self._color_hist.append(raw_color)
                    color = (collections.Counter(self._color_hist).most_common(1)[0][0]
                             if self._color_hist else None)
                self._ctx.update(people_count=count, color=color, scene=scene,
                                 obj_crop=obj_crop)
                self._update_presence(count)
            dt = time.time() - t0
            if dt < interval:
                time.sleep(interval - dt)

    def _infer(self, frame):
        res = self._model(frame, conf=config.SCENE_CONF, verbose=False)
        r = res[0]
        boxes = r.boxes
        if boxes is None or len(boxes) == 0:
            self.last_boxes = []
            return 0, None, None, None
        cls = boxes.cls.cpu().numpy().astype(int)
        conf = boxes.conf.cpu().numpy()
        xywh = boxes.xywh.cpu().numpy()
        names = r.names  # {idx: prompt}

        # 사람 카운트 + 가장 큰 사람 박스
        person_idxs = [i for i, c in enumerate(cls)
                       if names[c] == "person" and conf[i] >= config.PERSON_CONF]
        count = len(person_idxs)
        color = None
        if person_idxs:
            big = max(person_idxs, key=lambda i: xywh[i][2] * xywh[i][3])
            color = self._torso_color(frame, xywh[big])

        # 장면: 사람 외 감지 사물 한국어 라벨 (중복 제거) + 가장 큰 사물 크롭(teachable용)
        labels, seen = [], set()
        big_obj_area, big_obj_box = -1, None
        for i, c in enumerate(cls):
            en = names[c]
            if en == "person":
                continue
            ko = self._ko.get(en)
            if ko and ko not in seen:
                seen.add(ko)
                labels.append(ko)
            area = xywh[i][2] * xywh[i][3]
            if area > big_obj_area:
                big_obj_area, big_obj_box = area, xywh[i]
        scene = ", ".join(labels) if labels else None
        obj_crop = self._crop(frame, big_obj_box) if big_obj_box is not None else None
        # 프리뷰용 박스 (xywh→xyxy, 신뢰도 임계 통과분만)
        boxes = []
        for i, c in enumerate(cls):
            en = names[c]
            is_p = en == "person"
            if is_p and conf[i] < config.PERSON_CONF:
                continue
            cx, cy, bw, bh = xywh[i]
            boxes.append((int(cx - bw / 2), int(cy - bh / 2),
                          int(cx + bw / 2), int(cy + bh / 2),
                          "사람" if is_p else (self._ko.get(en) or en), is_p))
        self.last_boxes = boxes
        return count, color, scene, obj_crop

    def _crop(self, frame, box_xywh):
        h, w = frame.shape[:2]
        cx, cy, bw, bh = box_xywh
        x1, x2 = int(max(0, cx - bw / 2)), int(min(w, cx + bw / 2))
        y1, y2 = int(max(0, cy - bh / 2)), int(min(h, cy + bh / 2))
        if x2 <= x1 or y2 <= y1:
            return None
        return frame[y1:y2, x1:x2].copy()

    def _torso_color(self, frame, box_xywh):
        h, w = frame.shape[:2]
        cx, cy, bw, bh = box_xywh
        # 상체 ≈ 박스 상단 25~60% 높이, 중앙 40% 폭
        y1 = cy - bh * 0.25
        y2 = cy - bh * 0.10 + bh * 0.25
        x1 = cx - bw * 0.20
        x2 = cx + bw * 0.20
        x1, x2 = int(max(0, x1)), int(min(w, x2))
        y1, y2 = int(max(0, y1)), int(min(h, y2))
        if x2 <= x1 or y2 <= y1:
            return None
        return dominant_color_name(frame[y1:y2, x1:x2])

    def _update_presence(self, count):
        if count > 0:
            self._pos_frames += 1
            self._neg_frames = 0
            if not self._present and self._pos_frames >= config.PRESENT_FRAMES:
                self._present = True
                now = time.time()
                if config.GREET_ON_ARRIVAL and (now - self._last_greet) >= config.GREET_MIN_INTERVAL:
                    self._last_greet = now
                    self._on_new_person()
        else:
            self._neg_frames += 1
            self._pos_frames = 0
            if self._present and self._neg_frames >= config.ABSENT_FRAMES:
                self._present = False
