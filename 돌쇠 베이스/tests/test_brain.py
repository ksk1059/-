from dolswe.brain import build_messages, Brain
from dolswe.memory import ConversationMemory


def _mem():
    return ConversationMemory(path=None)  # 디스크 영속 끔 (테스트 격리)


def test_build_messages_no_persona_but_has_camera_ctx():
    mem = _mem()
    msgs = build_messages(mem, "안녕",
                          {"people_count": 1, "color": "빨간", "scene": None})
    # 페르소나 씨앗 0 → 카메라 상황 system 노트만, 마지막은 user
    assert msgs[-1] == {"role": "user", "content": "안녕"}
    assert any("빨간" in m["content"] for m in msgs)


def test_build_messages_uses_memory_history():
    mem = _mem()
    mem.add("user", "내 이름은 톨쇠야")
    mem.add("bot", "반가워")
    msgs = build_messages(mem, "기억해?", {})
    contents = [m["content"] for m in msgs]
    assert "내 이름은 톨쇠야" in contents
    assert msgs[-1]["content"] == "기억해?"


class FakeClient:
    def chat(self, model, messages, stream=True, **kwargs):
        for piece in ["반가", "워! ", "잘 ", "왔어."]:
            yield {"message": {"content": piece}}


def test_respond_yields_sentences():
    brain = Brain(client=FakeClient(), memory=_mem())
    snap = {"people_count": 0, "color": None, "scene": None}
    sentences = list(brain.respond("안녕", snap))
    assert sentences == ["반가워!", "잘 왔어."]


def test_respond_accumulates_memory():
    brain = Brain(client=FakeClient(), memory=_mem())
    snap = {"people_count": 0, "color": None, "scene": None}
    list(brain.respond("안녕", snap))
    roles = [t["role"] for t in brain.memory.hot]
    assert "user" in roles and "bot" in roles
