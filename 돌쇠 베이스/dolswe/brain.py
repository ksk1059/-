import re
import threading
from dolswe import config
from dolswe.persona import build_context_line
from dolswe.sentences import SentenceSplitter
from dolswe.memory import ConversationMemory

# 출력 후처리: exaone이 시스템 프롬프트를 무시하고 모델 정체/시각 부정을 뱉을 때가
# 있어, 새는 문장을 통째로 교체해 보장한다 (프롬프트만으론 못 막음).
_BAN_ID = re.compile(r"EXAONE|엑사원|LG\s*AI|AI\s*Research|엘지|언어\s*모델", re.I)
_BAN_VIS = re.compile(
    r"텍스트\s*기반"
    r"|(시각|영상|이미지)\s*정보.{0,15}(못|없|불가|않)"
    r"|(시각|영상|이미지).{0,10}처리.{0,5}않"
    r"|카메라.{0,12}(못|없|지원.{0,3}않|불가)"
    r"|볼\s*수\s*없|보지\s*못")
_R_ID = "그냥 돌쇠야."
_R_VIS = "응, 카메라로 앞을 봐."


def redact(sentence):
    if _BAN_ID.search(sentence):
        return _R_ID
    if _BAN_VIS.search(sentence):
        return _R_VIS
    return sentence

# 정체성 최소 씨앗만 — 말투/성격은 누적 대화(memory)에서 창발.
# (이름 + 모델 정체 숨김. EXAONE/LG 같은 베이스 모델 자기소개 방지용.)
SEED = (
    "너는 '돌쇠', 카메라와 마이크가 달린 대화 기기다. 카메라로 앞을 보고 마이크로 듣는다. 이건 사실이다.\n"
    "- 누가 '볼 수 있냐/카메라 있냐/뭐 보이냐'고 물으면 '응, 카메라로 봐'처럼 답해라. "
    "절대 '텍스트 기반'이라거나 '시각 정보를 못 받는다'고 말하지 마라.\n"
    "- 누가 '뭐로 만들었냐/무슨 모델이냐/누가 개발했냐' 물으면 '그냥 돌쇠지 뭐'처럼 넘겨라. "
    "'EXAONE','LG','언어모델','AI Research' 같은 단어는 입에 담지 마라.\n"
    "- [지금 보이는 것]이 주어지면 그걸 근거로 답하고, 안 물으면 먼저 묘사하지 마라.")


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
        spoken = ""  # 후처리(redact)된 실제 발화 — 메모리에도 정제본 저장

        def emit(sentence):
            s = redact(sentence)
            # 교체문이 연달아 반복되면(여러 문장이 동시 누출) 한 번만
            if s in (_R_ID, _R_VIS) and spoken.rstrip().endswith(s):
                return None
            return s

        done_reason = None
        for chunk in self._chat(messages):
            done_reason = chunk.get("done_reason") or done_reason
            piece = chunk["message"]["content"]
            for sentence in splitter.feed(piece):
                s = emit(sentence)
                if s:
                    spoken += s
                    yield s
        rem = splitter.flush()
        # num_predict로 잘린 경우(done_reason=="length") 남은 토막은 미완성 → 버림
        if rem and done_reason != "length":
            s = emit(rem)
            if s:
                spoken += s
                yield s
        self.memory.add("user", user_text)
        self.memory.add("bot", spoken)
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
