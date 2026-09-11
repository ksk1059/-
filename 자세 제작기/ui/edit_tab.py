from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QSpinBox,
)

LEG_LIST = ["LF", "RF", "LB", "RB"]


class EditTab(QWidget):
    def __init__(self, main_window):
        super().__init__()
        self.main = main_window
        self._angle_boxes = {}

        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("선택한 키프레임 편집 (타임라인에서 선택)"))

        for leg in LEG_LIST:
            row = QHBoxLayout()
            row.addWidget(QLabel(leg))
            box = QSpinBox()
            box.setRange(-9999, 9999)
            self._angle_boxes[leg] = box
            row.addWidget(box)
            row.addStretch(1)
            layout.addLayout(row)

        hold_row = QHBoxLayout()
        hold_row.addWidget(QLabel("유지시간(ms):"))
        self.hold_box = QSpinBox()
        self.hold_box.setRange(0, 60000)
        hold_row.addWidget(self.hold_box)
        hold_row.addStretch(1)
        layout.addLayout(hold_row)

        btn_row = QHBoxLayout()
        load_btn = QPushButton("선택 불러오기")
        load_btn.clicked.connect(self._load_selected)
        apply_btn = QPushButton("적용")
        apply_btn.clicked.connect(self._apply)
        btn_row.addWidget(load_btn)
        btn_row.addWidget(apply_btn)
        btn_row.addStretch(1)
        layout.addLayout(btn_row)
        layout.addStretch(1)

    def _current_kf(self):
        i = self.main.selected_keyframe_index()
        if 0 <= i < len(self.main.motion.keyframes):
            return i, self.main.motion.keyframes[i]
        return -1, None

    def _load_selected(self):
        _, kf = self._current_kf()
        if kf is None:
            return
        for leg in LEG_LIST:
            self._angle_boxes[leg].setValue(kf.angles[leg])
        self.hold_box.setValue(kf.hold_ms)

    def _apply(self):
        _, kf = self._current_kf()
        if kf is None:
            return
        for leg in LEG_LIST:
            kf.angles[leg] = self._angle_boxes[leg].value()
        kf.hold_ms = self.hold_box.value()
        self.main.refresh_timeline()
