import os

from poppy.motion import (
    Keyframe, Motion, keyframe_to_commands,
    save_motion, load_motion, list_motions, AngleMirror,
)


def sample_motion():
    return Motion(
        name="walk",
        keyframes=[
            Keyframe(angles={"LF": 90, "RF": -45, "LB": 0, "RB": 30}, hold_ms=500),
            Keyframe(angles={"LF": 0, "RF": 0, "LB": 0, "RB": 0}, hold_ms=300),
        ],
    )


def test_keyframe_to_commands_order():
    kf = Keyframe(angles={"LF": 90, "RF": -45, "LB": 0, "RB": 30}, hold_ms=0)
    assert keyframe_to_commands(kf) == [
        ("LF_ANGLE", 90), ("RF_ANGLE", -45),
        ("LB_ANGLE", 0), ("RB_ANGLE", 30),
    ]


def test_save_and_load_roundtrip(tmp_path):
    motion = sample_motion()
    path = save_motion(motion, str(tmp_path))
    assert os.path.exists(path)

    loaded = load_motion(path)
    assert loaded.name == "walk"
    assert len(loaded.keyframes) == 2
    assert loaded.keyframes[0].angles == {"LF": 90, "RF": -45, "LB": 0, "RB": 30}
    assert loaded.keyframes[0].hold_ms == 500


def test_list_motions(tmp_path):
    save_motion(Motion("b_motion", []), str(tmp_path))
    save_motion(Motion("a_motion", []), str(tmp_path))
    assert list_motions(str(tmp_path)) == ["a_motion", "b_motion"]


def test_mirror_rotate():
    m = AngleMirror()
    assert m.snapshot() == {"LF": 0, "RF": 0, "LB": 0, "RB": 0}
    m.rotate("LF", 15)
    m.rotate("LF", 15)
    assert m.snapshot()["LF"] == 30


def test_mirror_set_and_zero():
    m = AngleMirror()
    m.set_angle("RB", 90)
    assert m.snapshot()["RB"] == 90
    m.zero("RB")
    assert m.snapshot()["RB"] == 0


def test_mirror_snapshot_is_copy():
    m = AngleMirror()
    snap = m.snapshot()
    snap["LF"] = 999
    assert m.snapshot()["LF"] == 0
