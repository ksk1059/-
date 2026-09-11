import sys

from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QComboBox, QLabel, QListWidget, QTabWidget,
    QPlainTextEdit, QInputDialog, QMessageBox,
)

from poppy.controller import Controller
from poppy.motion import (
    Motion, Keyframe, AngleMirror, save_motion, load_motion, list_motions,
)
from ui.worker import PlaybackWorker
from ui.jog_tab import JogTab
from ui.edit_tab import EditTab

MOTIONS_DIR = "motions"
LEG_LIST = ["LF", "RF", "LB", "RB"]


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("4족 로봇 모션 에디터")
        self.resize(900, 600)

        self.controller = Controller()
        self.mirror = AngleMirror()
        self.motion = Motion(name="untitled", keyframes=[])
        self._worker = None
        self._playing_motion = None

        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)

        root.addLayout(self._build_connection_bar())

        body = QHBoxLayout()
        root.addLayout(body, stretch=1)

        left = QVBoxLayout()
        body.addLayout(left, stretch=3)

        self.tabs = QTabWidget()
        self.tabs.addTab(JogTab(self), "라이브 조그")
        self.tabs.addTab(EditTab(self), "오프라인 편집")
        left.addWidget(self.tabs, stretch=1)

        left.addLayout(self._build_timeline())

        body.addLayout(self._build_library(), stretch=1)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumHeight(120)
        root.addWidget(self.log)

        self.refresh_library()

    # --- 상단 연결 바 ---
    def _build_connection_bar(self):
        bar = QHBoxLayout()
        self.port_box = QComboBox()
        self.port_box.addItems(Controller.list_ports())
        refresh_btn = QPushButton("포트 새로고침")
        refresh_btn.clicked.connect(self._refresh_ports)
        self.connect_btn = QPushButton("연결")
        self.connect_btn.clicked.connect(self._toggle_connect)
        self.status_label = QLabel("미연결")

        bar.addWidget(QLabel("포트:"))
        bar.addWidget(self.port_box)
        bar.addWidget(refresh_btn)
        bar.addWidget(self.connect_btn)
        bar.addWidget(self.status_label)
        bar.addStretch(1)
        return bar

    def _refresh_ports(self):
        self.port_box.clear()
        self.port_box.addItems(Controller.list_ports())

    def _toggle_connect(self):
        if self.controller.is_connected():
            self.controller.close()
            self.connect_btn.setText("연결")
            self.status_label.setText("미연결")
            return
        port = self.port_box.currentText()
        if not port:
            QMessageBox.warning(self, "경고", "포트를 선택하세요.")
            return
        try:
            self.controller.connect(port)
            self.connect_btn.setText("해제")
            self.status_label.setText(f"연결됨: {port}")
            self._append_log(f"연결됨: {port}")
        except Exception as e:
            QMessageBox.critical(self, "연결 실패", str(e))

    # --- 하단 타임라인 ---
    def _build_timeline(self):
        box = QVBoxLayout()
        box.addWidget(QLabel("타임라인 (키프레임)"))
        self.timeline = QListWidget()
        box.addWidget(self.timeline)

        btns = QHBoxLayout()
        del_btn = QPushButton("삭제")
        del_btn.clicked.connect(self._delete_keyframe)
        dup_btn = QPushButton("복제")
        dup_btn.clicked.connect(self._duplicate_keyframe)
        play_btn = QPushButton("▶ 재생")
        play_btn.clicked.connect(self._play)
        stop_btn = QPushButton("■ 정지")
        stop_btn.clicked.connect(self._stop)
        for b in (del_btn, dup_btn, play_btn, stop_btn):
            btns.addWidget(b)
        box.addLayout(btns)
        return box

    def add_keyframe(self, angles, hold_ms):
        self.motion.keyframes.append(Keyframe(angles=dict(angles), hold_ms=hold_ms))
        self.refresh_timeline()

    def refresh_timeline(self):
        self.timeline.clear()
        for i, kf in enumerate(self.motion.keyframes):
            text = f"{i}: " + " ".join(f"{leg}{kf.angles[leg]:+d}" for leg in LEG_LIST)
            text += f"  hold={kf.hold_ms}ms"
            self.timeline.addItem(text)

    def selected_keyframe_index(self):
        return self.timeline.currentRow()

    def _delete_keyframe(self):
        i = self.selected_keyframe_index()
        if 0 <= i < len(self.motion.keyframes):
            del self.motion.keyframes[i]
            self.refresh_timeline()

    def _duplicate_keyframe(self):
        i = self.selected_keyframe_index()
        if 0 <= i < len(self.motion.keyframes):
            kf = self.motion.keyframes[i]
            self.motion.keyframes.insert(
                i + 1, Keyframe(angles=dict(kf.angles), hold_ms=kf.hold_ms)
            )
            self.refresh_timeline()

    # --- 우측 라이브러리 ---
    def _build_library(self):
        box = QVBoxLayout()
        box.addWidget(QLabel("라이브러리"))
        self.library = QListWidget()
        box.addWidget(self.library)

        new_btn = QPushButton("새로")
        new_btn.clicked.connect(self._new_motion)
        save_btn = QPushButton("저장")
        save_btn.clicked.connect(self._save_motion)
        load_btn = QPushButton("불러오기")
        load_btn.clicked.connect(self._load_motion)
        del_btn = QPushButton("삭제")
        del_btn.clicked.connect(self._delete_motion)
        for b in (new_btn, save_btn, load_btn, del_btn):
            box.addWidget(b)
        return box

    def refresh_library(self):
        self.library.clear()
        self.library.addItems(list_motions(MOTIONS_DIR))

    def _new_motion(self):
        name, ok = QInputDialog.getText(self, "새 동작", "이름:")
        if ok and name:
            self.motion = Motion(name=name, keyframes=[])
            save_motion(self.motion, MOTIONS_DIR)  # 즉시 파일 생성 → 라이브러리에 바로 표시
            self.refresh_timeline()
            self.refresh_library()
            self._append_log(f"새 동작 생성: {name}")

    def _save_motion(self):
        save_motion(self.motion, MOTIONS_DIR)
        self._append_log(f"저장됨: {self.motion.name}")
        self.refresh_library()

    def _load_motion(self):
        item = self.library.currentItem()
        if not item:
            return
        import os
        path = os.path.join(MOTIONS_DIR, item.text() + ".json")
        self.motion = load_motion(path)
        self.refresh_timeline()
        self._append_log(f"불러옴: {self.motion.name}")

    def _delete_motion(self):
        item = self.library.currentItem()
        if not item:
            return
        import os
        path = os.path.join(MOTIONS_DIR, item.text() + ".json")
        if os.path.exists(path):
            os.remove(path)
        self.refresh_library()

    # --- 재생 ---
    def _play(self):
        # 재생 중 재진입 방지 (실행 중 QThread를 새로 덮어쓰면 크래시)
        if self._worker is not None and self._worker.isRunning():
            return
        if not self.controller.is_connected():
            QMessageBox.warning(self, "경고", "먼저 아두이노에 연결하세요.")
            return
        if not self.motion.keyframes:
            return
        self._playing_motion = self.motion
        self._worker = PlaybackWorker(self.controller, self.motion)
        self._worker.log.connect(self._append_log)
        self._worker.error.connect(lambda s: self._append_log("ERROR: " + s))
        self._worker.progress.connect(lambda i: self.timeline.setCurrentRow(i))
        self._worker.done.connect(self._on_playback_done)
        self._worker.error.connect(self._on_playback_error)
        self._worker.stopped.connect(self._on_playback_stopped)
        self._set_playing(True)
        self._worker.start()

    def _stop(self):
        if self._worker is not None:
            self._worker.stop()

    def _set_playing(self, playing):
        # 재생 중에는 조그/편집 탭과 연결 해제 버튼을 잠가 시리얼 동시접근을 막는다
        self.tabs.setEnabled(not playing)
        self.connect_btn.setEnabled(not playing)

    def _on_playback_done(self):
        # 재생 성공: 미러를 재생한 동작의 마지막 키프레임 각도로 동기화 (I4)
        if self._playing_motion is not None and self._playing_motion.keyframes:
            last = self._playing_motion.keyframes[-1]
            for leg in LEG_LIST:
                self.mirror.set_angle(leg, last.angles[leg])
        self._finish_playback()

    def _on_playback_error(self, msg):
        self._finish_playback()

    def _on_playback_stopped(self):
        # 정지: 로봇이 마지막 키프레임에 도달하지 않았으므로 미러 동기화 안 함
        self._finish_playback()

    def _finish_playback(self):
        self._set_playing(False)
        # done/stopped는 run() 안에서 emit되므로, 참조를 지우기 전에 스레드가
        # 완전히 끝날 때까지 기다린다. 안 그러면 실행 중 QThread가 파괴돼 크래시난다.
        if self._worker is not None:
            self._worker.wait()
        self._worker = None
        self._playing_motion = None
        jog = self.tabs.widget(0)
        if hasattr(jog, "_refresh_labels"):
            jog._refresh_labels()

    def _append_log(self, text):
        self.log.appendPlainText(text)

    def closeEvent(self, event):
        # 재생 중 창을 닫으면 실행 중 QThread 파괴로 크래시 → 안전 종료 대기 (I3)
        if self._worker is not None and self._worker.isRunning():
            self._worker.stop()
            self._worker.wait(5000)
        super().closeEvent(event)


def main():
    app = QApplication(sys.argv)
    win = MainWindow()
    win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
