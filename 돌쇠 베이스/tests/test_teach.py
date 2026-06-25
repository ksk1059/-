from dolswe.teach import (TeachStore, parse_label, is_teaching,
                          normalize_hand, _cosine, _l2)


def test_parse_label_variants():
    assert parse_label("이 자세는 브이야") == "브이"
    assert parse_label("저거 텀블러야") == "텀블러"
    assert parse_label("방금 건 인사라고 불러") == "인사"
    assert parse_label("브이") == "브이"            # 맨 답
    assert parse_label("이건 손하트라고 해") == "손하트"


def test_parse_label_rejects_long_sentence():
    assert parse_label("오늘 날씨가 좋아서 산책을 나갔다 왔어 진짜 좋더라") is None


def test_is_teaching():
    assert is_teaching("이 자세는 브이야")
    assert is_teaching("저거 텀블러라고 불러")
    assert not is_teaching("너 이름이 뭐야")        # 질문은 가르침 아님


def test_store_add_match_l2(tmp_path):
    s = TeachStore(path=str(tmp_path / "t.json"))
    s.add("hand", [0.0, 0.0, 1.0], "브이")
    label, score = s.match("hand", [0.0, 0.0, 1.05], metric="l2", thresh=0.2)
    assert label == "브이"
    far, _ = s.match("hand", [5.0, 5.0, 5.0], metric="l2", thresh=0.2)
    assert far is None


def test_store_empty_returns_none():
    s = TeachStore(path=None)
    assert s.match("hand", [0.0], metric="l2") == (None, None)


def test_store_persist(tmp_path):
    p = tmp_path / "t.json"
    s1 = TeachStore(path=str(p))
    s1.add("object", [1.0, 0.0], "컵")
    s2 = TeachStore(path=str(p))
    assert s2.count("object") == 1
    assert s2.labels("object") == ["컵"]


def test_normalize_hand_scale_invariant():
    # 같은 모양, 다른 크기/위치 → 거의 같은 벡터
    class P:
        def __init__(s, x, y): s.x, s.y = x, y
    base = [P(i * 0.01, i * 0.01) for i in range(21)]
    big = [P(0.5 + i * 0.02, 0.5 + i * 0.02) for i in range(21)]
    from dolswe.teach import landmarks_to_xy
    v1 = normalize_hand(landmarks_to_xy(base))
    v2 = normalize_hand(landmarks_to_xy(big))
    assert _cosine(v1, v2) > 0.99
