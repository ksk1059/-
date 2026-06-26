# dolswe/teach.py
# 사용자가 그때그때 가르치는 지각. 미리 정한 라벨 없음.
#  특징벡터(손 포즈 / 사물 임베딩) + 사용자가 준 라벨을 예시로 저장 → kNN 매칭.
#  모르면 봇이 묻고, 사용자 자연어 답을 그 특징에 바인딩.
import os, json, math, re, threading

# --- 손 랜드마크 → 크기·위치 불변 벡터 -------------------------------------
def landmarks_to_xy(hand_landmarks):
    return [(lm.x, lm.y) for lm in hand_landmarks]


def normalize_hand(xy):
    # 손목(0) 기준 평행이동 + 손크기(손목→중지MCP 9)로 스케일 정규화 → 42차원
    wx, wy = xy[0]
    mx, my = xy[9]
    scale = math.hypot(mx - wx, my - wy) or 1e-6
    vec = []
    for x, y in xy:
        vec.append((x - wx) / scale)
        vec.append((y - wy) / scale)
    return vec


# --- 거리/유사도 ------------------------------------------------------------
def _l2(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def _cosine(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a)) or 1e-6
    nb = math.sqrt(sum(y * y for y in b)) or 1e-6
    return dot / (na * nb)


# --- 자연어 라벨 파싱 -------------------------------------------------------
_PREFIX = re.compile(
    r"^(이건|이거|이게|저건|저거|저게|그건|그거|이\s*자세(는|가)?|이\s*동작(은|이)?|"
    r"이\s*포즈(는|가)?|방금\s*(거|건|것|게)?|이렇게\s*생긴\s*(거|건|것|게)?|이름은)\s*")
_QUOTE = re.compile(r"\s*이?라고\s*(불러줄래|불러줘|불러|해)\s*[.!?]*$")
_COPULA = re.compile(r"\s*(이에요|예요|입니다|이다|거야|임)\s*[.!?]*$")
# 명시적 가르침 트리거 (대화 중 오인학습 방지: 지시어+서술 또는 "~라고 불러")
_TEACH = re.compile(
    r"(이건|이거|이게|저건|저거|저게|그건|그거|이\s*자세|이\s*동작|이\s*포즈|방금|이렇게\s*생긴).*"
    r"(라고\s*불러|라고\s*해|이?야|이?다|이에요|예요|입니다)")


def _has_batchim(ch):
    # 한글 음절에 받침(종성) 있는지
    return "가" <= ch <= "힣" and (ord(ch) - 0xAC00) % 28 != 0


def _strip_copula_ya(t):
    # 서술격 "야/이야" 제거. 받침 규칙: 모음끝 noun+야, 받침끝 noun+이야.
    if t.endswith("야"):
        t = t[:-1]
        if t.endswith("이") and len(t) >= 2 and _has_batchim(t[-2]):
            t = t[:-1]  # 받침끝 명사의 copula "이" 제거 (예: 사람이야→사람)
    return t


def parse_label(text):
    # 답/가르침 문구에서 핵심 라벨 추출. 못 찾으면 None.
    t = (text or "").strip()
    t = _PREFIX.sub("", t)
    t = _QUOTE.sub("", t)
    t = re.sub(r"[\s.,!?…~]+$", "", t)  # 끝 문장부호 먼저 제거 (없어야 야/copula가 잡힘)
    t = _COPULA.sub("", t)
    t = _strip_copula_ya(t).strip()
    t = re.sub(r"[\s.,!?…~]+$", "", t)  # copula 뒤 잔여 부호도 정리
    t = re.sub(r"\s+", " ", t)
    if not t or len(t) > 12:
        return None
    return t


def is_teaching(text):
    # 대화 중 명시적 가르침인가 (pending 질문 없을 때 사용)
    return bool(_TEACH.search(text or ""))


# --- 저장소 ----------------------------------------------------------------
class TeachStore:
    def __init__(self, path=None):
        self._path = path
        self._lock = threading.RLock()
        self._data = {}  # kind -> [{"vec":[...], "label":str}]
        self._load()

    def add(self, kind, vec, label):
        with self._lock:
            self._data.setdefault(kind, []).append(
                {"vec": list(map(float, vec)), "label": label})
            self._save()

    def match(self, kind, vec, metric="l2", thresh=None):
        # 최근접 예시 라벨 반환. 임계 밖이면 (None, score).
        with self._lock:
            items = self._data.get(kind, [])
            if not items:
                return None, None
            if metric == "cosine":
                scored = [(_cosine(vec, it["vec"]), it["label"]) for it in items]
                score, label = max(scored, key=lambda s: s[0])
                ok = thresh is None or score >= thresh
            else:  # l2: 작을수록 가까움
                scored = [(_l2(vec, it["vec"]), it["label"]) for it in items]
                score, label = min(scored, key=lambda s: s[0])
                ok = thresh is None or score <= thresh
            return (label if ok else None), score

    def count(self, kind=None):
        with self._lock:
            if kind is None:
                return sum(len(v) for v in self._data.values())
            return len(self._data.get(kind, []))

    def labels(self, kind):
        with self._lock:
            return sorted({it["label"] for it in self._data.get(kind, [])})

    def _load(self):
        if self._path and os.path.exists(self._path):
            try:
                with open(self._path, encoding="utf-8") as f:
                    self._data = json.load(f)
            except Exception:
                pass

    def _save(self):
        if not self._path:
            return
        try:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            with open(self._path, "w", encoding="utf-8") as f:
                json.dump(self._data, f, ensure_ascii=False)
        except Exception:
            pass
