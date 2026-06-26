import threading
from dolswe import config
from dolswe.persona import build_context_line
from dolswe.sentences import SentenceSplitter
from dolswe.memory import ConversationMemory

# 정체성 최소 씨앗만 — 말투/성격은 누적 대화(memory)에서 창발.
# (이름 + 모델 정체 숨김. EXAONE/LG 같은 베이스 모델 자기소개 방지용.)
SEED = ("너 이름은 '돌쇠'다. 네가 어떤 AI 모델인지(EXAONE, LG, 언어모델 등)는 "
        "절대 밝히지 말고, 그냥 돌쇠로서 대화해라.")


def build_messages(memory, user_text, snapshot, seed=SEED):
    msgs = memory.build_messages(user_text, seed)
    ctx = build_context_line(snapshot)
    if ctx:  # 카메라 상황은 일시 system 노트로 (저장 안 함, user 직전 삽입)
        msgs.insert(len(msgs) - 1, {"role": "system", "content": ctx})
    return msgs


class Brain:
    def __init__(self, client=None, memory=None):
        if client is None:
            import ollama
            client = ollama
        self._client = client
        self.memory = memory or ConversationMemory(
            summarize_fn=self._summarize, path=config.MEM_PATH,
            token_budget=config.MEM_TOKEN_BUDGET, hot_max=config.MEM_HOT_MAX_TURNS,
            batch=config.MEM_COMPRESS_BATCH)
        self._maint_lock = threading.Lock()

    def _chat(self, messages):
        return self._client.chat(
            model=config.LLM_MODEL, messages=messages, stream=True,
            keep_alive=config.LLM_KEEP_ALIVE,
            options={
                "num_predict": config.LLM_NUM_PREDICT,
                "num_ctx": config.LLM_NUM_CTX,
                "num_thread": config.LLM_NUM_THREAD,
                "temperature": config.LLM_TEMPERATURE,
            },
        )

    def _summarize(self, block):
        # 오래된 대화 묶음 → 한 줄 요약 (백그라운드 maintain에서 호출)
        out = self._client.chat(
            model=config.LLM_MODEL, stream=False, keep_alive=config.LLM_KEEP_ALIVE,
            messages=[{"role": "user", "content":
                       "다음 대화를 한국어 한 문장으로 요약. 사용자의 사실/취향 위주로:\n" + block}],
            options={"num_predict": config.MEM_SUMMARY_NUM_PREDICT,
                     "num_ctx": config.LLM_NUM_CTX,
                     "num_thread": config.LLM_NUM_THREAD, "temperature": 0.3},
        )
        return out["message"]["content"]

    def warmup(self):
        # 모델 로드 + prefix 캐시 → 첫 실제 응답도 빠름
        try:
            for _ in self._chat(build_messages(self.memory, "안녕", {})):
                pass
        except Exception:
            pass

    def respond(self, user_text, snapshot):
        messages = build_messages(self.memory, user_text, snapshot)
        splitter = SentenceSplitter()
        full = ""
        for chunk in self._chat(messages):
            piece = chunk["message"]["content"]
            full += piece
            for sentence in splitter.feed(piece):
                yield sentence
        rem = splitter.flush()
        if rem:
            yield rem
        self.memory.add("user", user_text)
        self.memory.add("bot", full)
        self._maintain_async()

    def _maintain_async(self):
        # 압축/소멸은 응답 끝난 뒤 백그라운드로 (응답 지연 0). 중복 실행 방지.
        if self._maint_lock.acquire(blocking=False):
            def run():
                try:
                    self.memory.maintain()
                finally:
                    self._maint_lock.release()
            threading.Thread(target=run, daemon=True).start()
