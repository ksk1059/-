import json
from dolswe.memory import (ConversationMemory, approx_tokens, extract_pin,
                           normalize_perspective)


def test_normalize_perspective():
    assert normalize_perspective("나는 너를 만들었어") == "사용자는 돌쇠를 만들었어"
    assert normalize_perspective("내 이름은 철수") == "사용자 이름은 철수"
    assert normalize_perspective("네가 날 도와줘") == "돌쇠가 사용자를 도와줘"
    # 단어 내부는 보존
    assert normalize_perspective("나무랑 나라") == "나무랑 나라"


def test_pin_normalizes_perspective(tmp_path):
    m = ConversationMemory(path=str(tmp_path / "m.json"))
    m.pin("나는 매운 걸 좋아해")
    assert any("사용자는 매운" in c["text"] for c in m.core)


def test_extract_pin():
    assert extract_pin("내 이름은 철수야 기억해") == "내 이름은 철수야"
    assert extract_pin("돌쇠는 매운 걸 좋아해 외워둬") == "돌쇠는 매운 걸 좋아해"
    assert extract_pin("잊지마 행사는 3층이야") == "행사는 3층이야"
    assert extract_pin("이거 기억나?") is None      # 질문은 고정 아님
    assert extract_pin("그냥 잡담이야") is None       # 트리거 없음


def test_core_pin_persists_and_injects(tmp_path):
    p = tmp_path / "m.json"
    m = ConversationMemory(path=str(p))
    assert m.pin("철수가 만들었어") is True
    assert m.pin("철수가 만들었어") is False          # 중복 거부
    msgs = m.build_messages("뭐였지", seed="")
    assert any("고정 기억" in x["content"] and "철수" in x["content"] for x in msgs)
    m2 = ConversationMemory(path=str(p))             # 재로드 영속
    assert any(c["text"] == "철수가 만들었어" for c in m2.core)


def test_core_survives_eviction(tmp_path):
    # 잔이 예산 초과로 소멸해도 core는 남음
    m = ConversationMemory(path=None, hot_max=1, batch=1, token_budget=5,
                           summarize_fn=lambda b: "요약")
    m.pin("영구 사실")
    for i in range(8):
        m.add("user", f"문장 {i} 어쩌고 길게 길게")
    m.maintain()
    assert any(c["text"] == "영구 사실" for c in m.core)


def test_add_and_build():
    m = ConversationMemory(path=None)
    m.add("user", "안녕")
    m.add("bot", "응 왔어")
    msgs = m.build_messages("뭐해", seed="")
    assert msgs[-1] == {"role": "user", "content": "뭐해"}
    assert {"role": "user", "content": "안녕"} in msgs
    assert {"role": "assistant", "content": "응 왔어"} in msgs


def test_fact_gets_higher_importance():
    m = ConversationMemory(path=None)
    fact = m._salience("user", "내 이름은 톨쇠야")
    chit = m._salience("bot", "음")
    assert fact > chit


def test_compress_old_into_warm():
    m = ConversationMemory(path=None, hot_max=2, batch=2,
                           summarize_fn=lambda block: "요약됨")
    for i in range(6):
        m.add("user", f"문장{i}")
    m.maintain()
    assert len(m.hot) <= 2
    assert m.warm and all(w["text"] == "요약됨" for w in m.warm)


def test_evict_when_over_budget():
    # 아주 작은 예산 → WARM이 잔 넘쳐 소멸
    m = ConversationMemory(path=None, hot_max=1, batch=1, token_budget=5,
                           summarize_fn=lambda block: "긴 요약 문장 하나 둘 셋 넷 다섯")
    for i in range(8):
        m.add("user", f"문장 {i} 어쩌고 저쩌고 길게")
    m.maintain()
    assert m._ctx_tokens() <= 5 or len(m.hot) == 1  # 예산 내로 수렴


def test_persistence_roundtrip(tmp_path):
    p = tmp_path / "mem.json"
    m1 = ConversationMemory(path=str(p))
    m1.add("user", "기억해줘")
    m1.maintain()  # _save 트리거
    assert p.exists()
    m2 = ConversationMemory(path=str(p))
    assert any(t["text"] == "기억해줘" for t in m2.hot)


def test_approx_tokens_monotonic():
    assert approx_tokens("a") >= 1
    assert approx_tokens("a" * 30) > approx_tokens("a")
