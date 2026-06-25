import json
from dolswe.memory import ConversationMemory, approx_tokens


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
