from poppy.controller import Controller


class FakeSerial:
    """readline이 미리 넣어둔 응답 줄을 순서대로 뱉는 가짜 시리얼."""

    def __init__(self, responses):
        self._responses = [r.encode("ascii") for r in responses]
        self.written = []
        self.closed = False

    @property
    def in_waiting(self):
        return len(self._responses)

    def readline(self):
        if self._responses:
            return self._responses.pop(0)
        return b""

    def write(self, data):
        self.written.append(data)

    def flush(self):
        pass

    def reset_input_buffer(self):
        pass

    def close(self):
        self.closed = True


def make_controller(responses):
    ctrl = Controller()
    ctrl._serial = FakeSerial(responses)
    return ctrl


def test_send_packet_ok():
    ctrl = make_controller(["LF A 90.0", "OK"])
    ok, lines = ctrl.send_packet("@01LFA+0090\n")
    assert ok is True
    assert "OK" in lines
    assert ctrl._serial.written == [b"@01LFA+0090\n"]


def test_send_packet_err():
    ctrl = make_controller(["ERR: length mismatch"])
    ok, lines = ctrl.send_packet("@01BAD\n")
    assert ok is False
    assert any(line.startswith("ERR") for line in lines)


def test_send_packet_timeout():
    ctrl = make_controller([])  # 응답 없음
    ok, lines = ctrl.send_packet("@01LFA+0090\n", timeout_sec=0.05)
    assert ok is False


def test_send_commands_builds_packet():
    ctrl = make_controller(["OK"])
    ok, _ = ctrl.send_commands([("LF_ANGLE", 90)])
    assert ok is True
    assert ctrl._serial.written == [b"@01LFA+0090\n"]


def test_is_connected():
    ctrl = Controller()
    assert ctrl.is_connected() is False
    ctrl._serial = FakeSerial(["OK"])
    assert ctrl.is_connected() is True


def test_close():
    ctrl = make_controller(["OK"])
    fake = ctrl._serial
    ctrl.close()
    assert ctrl.is_connected() is False
    assert fake.closed is True
