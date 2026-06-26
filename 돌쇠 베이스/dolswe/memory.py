# dolswe/memory.py
# "잔" 메모리: 대화를 누적하되 사람 망각처럼 오래되고 덜 중요한 건 압축→소멸.
#  HOT(최근 원문) → WARM(요약) → 윈도우(토큰예산) 넘으면 낮은 중요도부터 제거.
import os, json, re, threading, time

# 사용자 사실/취향 신호 (있으면 중요도↑ → 오래 살아남음)
_FACT = re.compile(
    r"좋아|싫어|이름|나는|난\s|내가|취미|전공|사는|살아|일해|먹|관심|꿈|목표|살이|학교|회사")

# 말투 기본값 (부모가 안 가르쳐도 격식체 탈피) + 본보기 보관 상한
DEFAULT_STYLE = "친근한 반말로 짧고 자연스럽게. 존댓말·격식체·딱딱한 설명체 금지. 이모지 남발 금지."
STYLE_EXEMPLAR_MAX = 8

# 말투 지시 트리거 ("말투 ~", "이렇게 말해", "~하게 말해")
_STYLE_CMD = re.compile(r"말투|이렇게\s*말해|말하는\s*법|이런\s*식으로\s*말|반말로|더\s*\S+하게\s*말")
# 학습된 말투가 존댓말 지향이면 반말 강제(banmalify)를 끔 → 후처리가 학습에 종속됨
_FORMAL_HINT = re.compile(r"존댓말|높임|정중|공손|예의|격식")
_CASUAL_HINT = re.compile(r"반말")


def extract_style(text):
    # 명시적 말투 지시면 규칙 내용 반환, 아니면 None
    t = (text or "").strip()
    if not _STYLE_CMD.search(t) or t.endswith("?"):
        return None
    rule = re.sub(r"말투(는|를|은|이|가)?|이렇게\s*말해(줘|라)?|말하는\s*법|이런\s*식으로\s*말해?",
                  "", t)
    rule = re.sub(r"\s+", " ", rule).strip(" :,.!~·-")
    return rule if len(rule) >= 2 else t  # 너무 짧으면 원문 통째 규칙으로


# 영구 고정("기억해/외워/잊지마") 트리거 + 사실 추출
_PIN = re.compile(
    r"(영구(히|하게)?\s*)?(기억\s*해\s*(둬|줘|주라|줄래)?|외워\s*(둬|줘)?|잊지\s*마라?|명심해|꼭\s*기억해?)")

# 관점 정규화: 고정 기억은 "나/너"를 3인칭으로 바꿔 봇이 주어를 헷갈리지 않게.
# (?<![가-힣])...(?![가-힣]) 로 단어 내부(나무, 나라)는 건드리지 않음.
_PERSPECTIVE = [
    (re.compile(r"(?<![가-힣])내가(?![가-힣])"), "사용자가"),
    (re.compile(r"(?<![가-힣])나는(?![가-힣])"), "사용자는"),
    (re.compile(r"(?<![가-힣])나도(?![가-힣])"), "사용자도"),
    (re.compile(r"(?<![가-힣])나를(?![가-힣])"), "사용자를"),
    (re.compile(r"(?<![가-힣])나의(?![가-힣])"), "사용자의"),
    (re.compile(r"(?<![가-힣])날(?![가-힣])"), "사용자를"),
    (re.compile(r"(?<![가-힣])내(?=\s)"), "사용자"),
    (re.compile(r"(?<![가-힣])나(?![가-힣])"), "사용자"),
    (re.compile(r"(?<![가-힣])너는(?![가-힣])"), "돌쇠는"),
    (re.compile(r"(?<![가-힣])너를(?![가-힣])"), "돌쇠를"),
    (re.compile(r"(?<![가-힣])너의(?![가-힣])"), "돌쇠의"),
    (re.compile(r"(?<![가-힣])네가(?![가-힣])"), "돌쇠가"),
    (re.compile(r"(?<![가-힣])니가(?![가-힣])"), "돌쇠가"),
    (re.compile(r"(?<![가-힣])널(?![가-힣])"), "돌쇠를"),
    (re.compile(r"(?<![가-힣])너(?![가-힣])"), "돌쇠"),
]


def normalize_perspective(text):
    for pat, repl in _PERSPECTIVE:
        text = pat.sub(repl, text)
    return text


def extract_pin(text):
    # "기억해" 류 명령이면 트리거 빼고 남는 사실 반환. 질문(?)이나 빈 내용이면 None.
    t = (text or "").strip()
    if not _PIN.search(t) or t.endswith("?"):
        return None
    fact = _PIN.sub("", t)
    fact = re.sub(r"\s+", " ", fact).strip(" ,.!~·-")
    return fact if len(fact) >= 2 else None


def approx_tokens(s):
    # 토크나이저 없이 대략치 (한·영 혼용 보수적). 예산 비교용이라 정밀할 필요 없음.
    return max(1, len(s) // 3)


class ConversationMemory:
    def __init__(self, summarize_fn=None, path=None, token_budget=2800,
                 hot_max=12, batch=4):
        self._summarize = summarize_fn
        self._path = path
        self._budget = token_budget
        self._hot_max = hot_max
        self._batch = batch
        self._lock = threading.RLock()
        self.hot = []   # [{role, text, ts, imp}]  role: "user"|"bot"
        self.warm = []  # [{text, ts, imp}]  압축된 요약
        self.core = []  # [{text, ts}]  영구 고정 기억 — 절대 소멸 안 함, 항상 주입
        self.style = []  # [{kind:'directive'|'exemplar', text, ts}]  말투 (부모가 학습)
        self._load()

    # --- 말투(부모가 가르치는 화법) ---
    def add_style(self, kind, text):
        text = (text or "").strip()
        if not text:
            return False
        with self._lock:
            if any(s["kind"] == kind and s["text"] == text for s in self.style):
                return False
            self.style.append({"kind": kind, "text": text, "ts": time.time()})
            if kind == "exemplar":  # 본보기는 최근 N개만 (롤링)
                ex = [s for s in self.style if s["kind"] == "exemplar"]
                for old in ex[:-STYLE_EXEMPLAR_MAX]:
                    self.style.remove(old)
            self._save()
            return True

    def register(self):
        # 학습된 말투의 격식 수준. 부모가 존댓말을 가르쳤으면 'formal' → 반말강제 끔.
        # 가장 최근 지시 우선. 기본은 'casual'(반말).
        for s in reversed(self.style):
            if s["kind"] != "directive":
                continue
            if _CASUAL_HINT.search(s["text"]):
                return "casual"
            if _FORMAL_HINT.search(s["text"]):
                return "formal"
        return "casual"

    def style_block(self):
        directives = [s["text"] for s in self.style if s["kind"] == "directive"] or [DEFAULT_STYLE]
        exem = [s["text"] for s in self.style if s["kind"] == "exemplar"]
        txt = ("지금부터 아래 말투로만 대답해라. 직전까지 격식체/존댓말로 말했어도 무조건 이 말투로 바꿔라.\n"
               "말투 규칙: " + " / ".join(directives))
        if exem:
            txt += ("\n이 사람 말투를 그대로 흉내내라 (어휘·어미·분위기):\n"
                    + "\n".join(f"- {e}" for e in exem))
        return txt

    # --- 영구 고정 기억 ---
    def pin(self, text):
        text = normalize_perspective((text or "").strip())  # 나→사용자, 너→돌쇠
        if not text:
            return False
        with self._lock:
            if any(c["text"] == text for c in self.core):  # 중복 방지
                return False
            self.core.append({"text": text, "ts": time.time()})
            self._save()
            return True

    # --- 중요도 ---
    def _salience(self, role, text):
        imp = 1.0
        if _FACT.search(text):
            imp += 2.0                       # 사용자 사실/취향 = 오래 보존
        if "?" in text or "？" in text:
            imp += 0.5                       # 질문 = 맥락 앵커
        imp += min(len(text) // 60, 1) * 0.5
        if role == "user":
            imp += 0.5                       # 사용자 발화가 봇 발화보다 우선
        return imp

    # --- 누적 ---
    def add(self, role, text, ts=None):
        text = (text or "").strip()
        if not text:
            return
        ts = time.time() if ts is None else ts
        with self._lock:
            self.hot.append({"role": role, "text": text, "ts": ts,
                             "imp": self._salience(role, text)})

    # --- 프롬프트 구성 ---
    def build_messages(self, user_text, seed=""):
        with self._lock:
            msgs = []
            if seed:
                msgs.append({"role": "system", "content": seed})
            if self.core:  # 영구 고정 기억 — 항상 먼저 주입
                lines = "\n".join(f"- {c['text']}" for c in self.core)
                msgs.append({"role": "system",
                             "content": "[고정 기억] (사용자=상대, 돌쇠=너 자신)\n" + lines})
            if self.warm:
                lines = "\n".join(f"- {w['text']}" for w in self.warm)
                msgs.append({"role": "system", "content": "[기억]\n" + lines})
            for t in self.hot:
                role = "assistant" if t["role"] == "bot" else "user"
                msgs.append({"role": role, "content": t["text"]})
            # 말투는 가장 마지막 시스템 지시로 (이력의 격식체를 최근성으로 덮어씀)
            msgs.append({"role": "system", "content": self.style_block()})
            msgs.append({"role": "user", "content": user_text})
            return msgs

    def _ctx_tokens(self):
        return (sum(approx_tokens(w["text"]) + 2 for w in self.warm)
                + sum(approx_tokens(t["text"]) + 2 for t in self.hot))

    # --- 유지보수: 압축 + 소멸 (백그라운드 호출) ---
    def maintain(self):
        with self._lock:
            # 1) HOT이 한도 넘으면 오래된 것부터 batch씩 WARM 요약으로
            while len(self.hot) > self._hot_max:
                batch = self.hot[:self._batch]
                self.hot = self.hot[self._batch:]
                gist = self._compress(batch)
                if gist:
                    self.warm.append({"text": gist,
                                      "ts": batch[-1]["ts"],
                                      "imp": max(b["imp"] for b in batch)})
            # 2) 토큰 예산 초과 → WARM에서 낮은 중요도·오래된 것부터 소멸 (잔 넘침)
            while self._ctx_tokens() > self._budget and self.warm:
                self.warm.sort(key=lambda w: (w["imp"], w["ts"]))
                self.warm.pop(0)
            # 3) 극단적으로 여전히 초과면 가장 오래된 HOT 제거
            while self._ctx_tokens() > self._budget and len(self.hot) > 1:
                self.hot.pop(0)
            self._save()

    def _compress(self, batch):
        block = "\n".join(
            f'{"돌쇠" if b["role"] == "bot" else "사용자"}: {b["text"]}' for b in batch)
        if self._summarize:
            try:
                gist = (self._summarize(block) or "").strip()
                if gist:
                    return gist
            except Exception:
                pass
        return block.replace("\n", " / ")[:80]  # 요약 실패 시 잘라서라도 남김

    # --- 영속화 ---
    def _load(self):
        if self._path and os.path.exists(self._path):
            try:
                with open(self._path, encoding="utf-8") as f:
                    d = json.load(f)
                self.hot = d.get("hot", [])
                self.warm = d.get("warm", [])
                self.core = d.get("core", [])
                self.style = d.get("style", [])
            except Exception:
                pass

    def _save(self):
        if not self._path:
            return
        try:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            with open(self._path, "w", encoding="utf-8") as f:
                json.dump({"hot": self.hot, "warm": self.warm, "core": self.core,
                           "style": self.style}, f, ensure_ascii=False)
        except Exception:
            pass
