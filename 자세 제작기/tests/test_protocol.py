import pytest
from poppy import protocol


def test_encode_angle_positive():
    assert protocol.encode_command("LF_ANGLE", 90) == "LFA+0090"


def test_encode_angle_negative():
    assert protocol.encode_command("RF_ANGLE", -45) == "RFA-0045"


def test_encode_down_cm():
    assert protocol.encode_command("RF_DOWN", 2) == "RFD00200"


def test_encode_up_fractional_cm():
    assert protocol.encode_command("LF_UP", 0.9) == "LFU00090"


def test_encode_zero():
    assert protocol.encode_command("LB_ZERO") == "LBRE00000"


def test_encode_rotate_alias():
    assert protocol.encode_command("LB_ROTATE", 90) == "LBR+0090"


def test_encode_bad_leg():
    with pytest.raises(ValueError):
        protocol.encode_command("XX_ANGLE", 0)


def test_encode_bad_action():
    with pytest.raises(ValueError):
        protocol.encode_command("LF_JUMP", 0)


def test_encode_angle_out_of_range():
    with pytest.raises(ValueError):
        protocol.encode_command("LF_ANGLE", 10000)


def test_make_packet_single():
    assert protocol.make_packet([("LF_ANGLE", 90)]) == "@01LFA+0090\n"


def test_make_packet_four():
    packet = protocol.make_packet([
        ("LF_ANGLE", 90), ("RF_ANGLE", -45),
        ("LB_ANGLE", 0), ("RB_ANGLE", 30),
    ])
    assert packet == "@04LFA+0090RFA-0045LBA+0000RBA+0030\n"


def test_make_packet_empty_raises():
    with pytest.raises(ValueError):
        protocol.make_packet([])


def test_make_packet_too_many_raises():
    with pytest.raises(ValueError):
        protocol.make_packet([("LF_ANGLE", 0)] * 7)
