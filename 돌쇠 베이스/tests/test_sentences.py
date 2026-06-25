from dolswe.sentences import SentenceSplitter

def test_emits_on_punctuation():
    s = SentenceSplitter()
    out = []
    for tok in ["안녕", "하세요", ". ", "반가", "워!"]:
        out += s.feed(tok)
    assert out == ["안녕하세요.", "반가워!"]

def test_flush_returns_remainder():
    s = SentenceSplitter()
    s.feed("어이 ")
    s.feed("거기")
    assert s.flush() == "어이 거기"

def test_flush_empty_returns_none():
    s = SentenceSplitter()
    s.feed("끝.")
    assert s.flush() is None

def test_long_run_splits_by_length():
    s = SentenceSplitter()
    out = s.feed("가" * 70)
    assert len(out) == 1
    assert len(out[0]) >= 60
