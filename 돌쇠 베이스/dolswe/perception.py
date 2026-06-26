# dolswe/perception.py
# 사용자가 그때그때 가르치는 지각. 미리 정한 라벨 없음.
#  손 포즈(MediaPipe) / 사물(YOLO 크롭 + CLIP) 특징 → 배운 예시와 kNN 매칭.
#  지속적으로 모르면 on_unknown 콜백 → 봇이 "이거 뭐야?" 물어봄.
import threading, time
import cv2
import mediapipe as mp
from dolswe import config
from dolswe.teach import normalize_hand, landmarks_to_xy


class Perception:
    def __init__(self, ctx, get_frame, store, on_unknown):
        self._ctx = ctx
        self._get = get_frame
        self._store = store
        self._on_unknown = on_unknown
        self._running = False
        self._thread = None
        self._hands = mp.solutions.hands.Hands(
            max_num_hands=2, min_detection_confidence=0.4, min_tracking_confidence=0.4)
        self._clip = None          # lazy 로드 (사물 처음 볼 때만)
        self._clip_pre = None
        self._torch = None
        self._last_kind = None     # 지금 두드러진 특징 (가르침 바인딩용)
        self._last_vec = None
        self._unknown_count = 0
        self._last_ask = -1e9
        self.last_hand_pts = None  # 프리뷰용: 손 랜드마크 [(x,y) 정규화] 또는 None

    def start(self):
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False
        try:
            self._hands.close()
        except Exception:
            pass

    # 가르침 바인딩: 지금 보이는 (kind, vec)
    def current_feature(self):
        return self._last_kind, self._last_vec

    def teach(self, label, kind=None, vec=None):
        if kind is None or vec is None:
            kind, vec = self._last_kind, self._last_vec
        if kind and vec is not None:
            self._store.add(kind, vec, label)
            return kind
        return None

    # --- 루프 ---
    def _loop(self):
        interval = 1.0 / config.PERCEPT_FPS
        while self._running:
            t0 = time.time()
            frame = self._get()
            if frame is not None:
                self._step(frame)
            dt = time.time() - t0
            if dt < interval:
                time.sleep(interval - dt)

    def _step(self, frame):
        kind, vec = self._extract(frame)
        self._last_kind, self._last_vec = kind, vec
        if vec is None:
            self._ctx.update(percept=None)
            self._unknown_count = 0
            return
        label, _ = self._match(kind, vec)
        self._ctx.update(percept=label)
        if label is None:
            self._unknown_count += 1
            now = time.time()
            if (self._unknown_count >= config.PERCEPT_STABLE_FRAMES
                    and now - self._last_ask >= config.PERCEPT_ASK_INTERVAL):
                self._last_ask = now
                self._unknown_count = 0
                self._on_unknown(kind, vec)
        else:
            self._unknown_count = 0

    # --- 특징 추출: 손 우선, 없으면 사물 크롭(vision_world가 ctx에 넣음) ---
    def _extract(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        res = self._hands.process(rgb)
        if res.multi_hand_landmarks:
            hands_xy = [landmarks_to_xy(h.landmark) for h in res.multi_hand_landmarks]
            self.last_hand_pts = hands_xy  # 프리뷰: 검출된 손 전부
            if len(hands_xy) >= 2:
                # 손목 x로 좌→우 정렬해 순서 일관화 후 두 손 벡터 결합 (84차원)
                two = sorted(hands_xy, key=lambda xy: xy[0][0])[:2]
                return "hand2", normalize_hand(two[0]) + normalize_hand(two[1])
            return "hand", normalize_hand(hands_xy[0])
        self.last_hand_pts = None
        crop = self._ctx.snapshot().get("obj_crop")
        if crop is not None and crop.size:
            emb = self._embed(crop)
            if emb is not None:
                return "object", emb
        return None, None

    def _match(self, kind, vec):
        if kind == "hand":
            return self._store.match("hand", vec, "l2", config.HAND_MATCH_THRESH)
        if kind == "hand2":  # 84차원 → L2 거리 스케일 ↑ 보정
            return self._store.match("hand2", vec, "l2", config.HAND_MATCH_THRESH * 1.5)
        return self._store.match("object", vec, "cosine", config.OBJ_MATCH_THRESH)

    def _embed(self, crop):
        try:
            if self._clip is None:
                import clip, torch
                self._torch = torch
                self._clip, self._clip_pre = clip.load(config.CLIP_MODEL, device="cpu")
                self._clip.eval()
            from PIL import Image
            img = Image.fromarray(cv2.cvtColor(crop, cv2.COLOR_BGR2RGB))
            x = self._clip_pre(img).unsqueeze(0)
            with self._torch.no_grad():
                f = self._clip.encode_image(x)[0]
            f = f / f.norm()
            return f.cpu().tolist()
        except Exception:
            return None
