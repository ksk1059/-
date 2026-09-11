from poppy.motion import Motion, Keyframe
from ui.worker import PlaybackWorker


class FakeController:
    def __init__(self, results):
        self._results = results
        self.sent = []

    def send_commands(self, commands, timeout_sec=30):
        self.sent.append(commands)
        return self._results.pop(0)


def collect_signals(worker):
    events = {"progress": [], "log": [], "error": [], "done": []}
    worker.progress.connect(lambda i: events["progress"].append(i))
    worker.log.connect(lambda s: events["log"].append(s))
    worker.error.connect(lambda s: events["error"].append(s))
    worker.done.connect(lambda: events["done"].append(True))
    return events


def two_frame_motion():
    return Motion("m", [
        Keyframe({"LF": 0, "RF": 0, "LB": 0, "RB": 0}, hold_ms=0),
        Keyframe({"LF": 90, "RF": 0, "LB": 0, "RB": 0}, hold_ms=0),
    ])


def test_playback_all_ok(qapp):
    ctrl = FakeController([(True, ["OK"]), (True, ["OK"])])
    worker = PlaybackWorker(ctrl, two_frame_motion())
    events = collect_signals(worker)
    worker.run()  # 동기 실행
    assert events["progress"] == [0, 1]
    assert events["done"] == [True]
    assert events["error"] == []
    assert len(ctrl.sent) == 2


def test_playback_stops_on_err(qapp):
    ctrl = FakeController([(False, ["ERR: bad"])])
    worker = PlaybackWorker(ctrl, two_frame_motion())
    events = collect_signals(worker)
    worker.run()
    assert events["error"]  # 에러 emit됨
    assert events["done"] == []
    assert len(ctrl.sent) == 1  # 두 번째 키프레임은 전송 안 함


def test_playback_emits_error_on_exception(qapp):
    class BoomController:
        def send_commands(self, commands, timeout_sec=30):
            raise RuntimeError("boom")

    worker = PlaybackWorker(BoomController(), two_frame_motion())
    events = collect_signals(worker)
    worker.run()
    assert events["error"]        # 예외가 error 시그널로 변환됨
    assert events["done"] == []   # done은 발행되지 않음


def test_playback_stop_emits_stopped_not_done(qapp):
    ctrl = FakeController([(True, ["OK"]), (True, ["OK"])])
    worker = PlaybackWorker(ctrl, two_frame_motion())
    events = {"stopped": [], "done": []}
    worker.stopped.connect(lambda: events["stopped"].append(True))
    worker.done.connect(lambda: events["done"].append(True))
    worker.stop()      # 첫 키프레임 전에 정지 요청
    worker.run()
    assert events["stopped"] == [True]
    assert events["done"] == []
    assert ctrl.sent == []   # 정지로 아무 키프레임도 전송 안 됨
