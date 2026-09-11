from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QSpinBox,
    QMessageBox,
)

LEG_LIST = ["LF", "RF", "LB", "RB"]


class JogTab(QWidget):
    def __init__(self, main_window):
        super().__init__()
        self.main = main_window
        self._angle_labels = {}

        layout = QVBoxLayout(self)

        step_row = QHBoxLayout()
        step_row.addWidget(QLabel("조그 스텝(도):"))
        self.step_box = QSpinBox()
        self.step_box.setRange(1, 180)
        self.step_box.setValue(15)
        step_row.addWidget(self.step_box)
        step_row.addStretch(1)
        layout.addLayout(step_row)

        for leg in LEG_LIST:
            layout.addLayout(self._build_leg_row(leg))

        cap_row = QHBoxLayout()
        cap_row.addWidget(QLabel("유지시간(ms):"))
        self.hold_box = QSpinBox()
        self.hold_box.setRange(0, 60000)
        self.hold_box.setValue(500)
        cap_row.addWidget(self.hold_box)
        capture_btn = QPushButton("현재 자세 캡처")
        capture_btn.clicked.connect(self._capture)
        cap_row.addWidget(capture_btn)
        cap_row.addStretch(1)
        layout.addLayout(cap_row)
        layout.addStretch(1)

        self._refresh_labels()

    def _build_leg_row(self, leg):
        row = QHBoxLayout()
        row.addWidget(QLabel(leg))
        minus = QPushButton("◄ -")
        minus.clicked.connect(lambda: self._jog(leg, -self.step_box.value()))
        plus = QPushButton("+ ►")
        plus.clicked.connect(lambda: self._jog(leg, self.step_box.value()))
        zero = QPushButton("0점(Z)")
        zero.clicked.connect(lambda: self._zero(leg))
        label = QLabel("0")
        self._angle_labels[leg] = label
        row.addWidget(minus)
        row.addWidget(label)
        row.addWidget(plus)
        row.addWidget(zero)
        row.addStretch(1)
        return row

    def _require_connection(self):
        if not self.main.controller.is_connected():
            QMessageBox.warning(self, "경고", "먼저 아두이노에 연결하세요.")
            return False
        return True

    def _jog(self, leg, delta):
        if not self._require_connection():
            return
        # 실제 펌웨어 패킷엔 상대회전이 없으므로 절대각도(A)로 보낸다:
        # 목표각 = 현재각 + delta
        target = self.main.mirror.snapshot()[leg] + delta
        ok, lines = self.main.controller.send_commands([(f"{leg}_ANGLE", target)])
        for line in lines:
            self.main._append_log(line)
        if ok:
            self.main.mirror.set_angle(leg, target)
            self._refresh_labels()

    def _zero(self, leg):
        if not self._require_connection():
            return
        ok, lines = self.main.controller.send_commands([(f"{leg}_ZERO",)])
        for line in lines:
            self.main._append_log(line)
        if ok:
            self.main.mirror.zero(leg)
            self._refresh_labels()

    def _capture(self):
        self.main.add_keyframe(self.main.mirror.snapshot(), self.hold_box.value())

    def _refresh_labels(self):
        snap = self.main.mirror.snapshot()
        for leg in LEG_LIST:
            self._angle_labels[leg].setText(str(snap[leg]))
