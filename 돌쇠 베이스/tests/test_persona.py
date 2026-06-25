from dolswe.persona import build_context_line


def test_context_line_includes_people_and_color():
    line = build_context_line({"people_count": 2, "color": "빨간", "scene": None})
    assert "2" in line and "빨간" in line


def test_context_line_empty_when_nothing():
    line = build_context_line({"people_count": 0, "color": None, "scene": None})
    assert line == ""
