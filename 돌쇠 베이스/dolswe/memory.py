# dolswe/memory.py
# "잔" 메모리: 대화를 누적하되 사람 망각처럼 오래되고 덜 중요한 건 압축→소멸.
#  HOT(최근 원문) → WARM(요약) → 윈도우(토큰예산) 넘으면 낮은 중요도부터 제거.
import os, json, re, threading, time

# 사용자 사실/취향 신호 (있으면 중요도↑ → 오래 살아남음)
_FACT = re.compile(
    r"좋아|싫어|이름|나는|난\s|내가|취미|전공|사는|살아|일해|먹|관심|꿈|목표|살이|학교|회사")


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
        self._load()

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
            if self.warm:
                lines = "\n".join(f"- {w['text']}" for w in self.warm)
                msgs.append({"role": "system", "content": "[기억]\n" + lines})
            for t in self.hot:
                role = "assistant" if t["role"] == "bot" else "user"
                msgs.append({"role": role, "content": t["text"]})
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
            except Exception:
                pass

    def _save(self):
        if not self._path:
            return
        try:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            with open(self._path, "w", encoding="utf-8") as f:
                json.dump({"hot": self.hot, "warm": self.warm}, f, ensure_ascii=False)
        except Exception:
            pass
