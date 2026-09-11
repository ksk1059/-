import time

from PySide6.QtCore import QThread, Signal

from poppy.motion import keyframe_to_commands


class PlaybackWorker(QThread):
    progress = Signal(int)
    log = Signal(str)
    error = Signal(str)
    done = Signal()
    stopped = Signal()

    def __init__(self, controller, motion):
        super().__init__()
        self._controller = controller
        self._motion = motion
        self._stop = False

    def stop(self):
        self._stop = True

    def run(self):
        try:
            for i, kf in enumerate(self._motion.keyframes):
                if self._stop:
                    self.log.emit("정지됨")
                    self.stopped.emit()
                    return

                ok, lines = self._controller.send_commands(keyframe_to_commands(kf))
                for line in lines:
                    self.log.emit(line)

                if not ok:
                    self.error.emit(f"키프레임 {i} 실패")
                    return

                self.progress.emit(i)

                if kf.hold_ms > 0:
                    time.sleep(kf.hold_ms / 1000.0)

            self.done.emit()
        except Exception as e:
            self.error.emit(str(e))
