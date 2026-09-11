# 4족 로봇 모션 에디터 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** UI로 4개 스텝모터를 조작해 키프레임 동작을 만들고 JSON으로 저장/재생하는 PySide6 데스크톱 툴을 만든다.

**Architecture:** 기존 단일 `.py`를 순수 프로토콜(`poppy/protocol.py`) → 시리얼(`poppy/controller.py`) → 모션 데이터(`poppy/motion.py`) 3계층 라이브러리로 분해하고, 그 위에 PySide6 UI(`ui/`)를 올린다. 라이브러리 계층은 TDD로 검증, UI는 launch 스모크 + 수동 시나리오로 검증한다. 재생은 QThread 워커에서 실행해 GUI 프리즈를 막는다.

**Tech Stack:** Python 3.13, PySide6, pyserial 3.5, pytest 8.4.

## Global Constraints

- Python 3.13, pyserial 3.5, PySide6, pytest 8.4 — 이미 설치됨.
- 다리 코드: `LF`, `RF`, `LB`, `RB` (순서 고정).
- 명령 개수 1~6개. 패킷은 `@` + 2자리 개수 + 8글자 블록들 + `\n`.
- 키프레임은 절대각도(`A` 명령)만 저장한다. cm(`U`/`D`) 높이는 키프레임에 넣지 않는다.
- 아두이노 펌웨어(`poppy_arduino_code.ino`)는 변경하지 않는다.
- 보드레이트 115200.
- 코드/식별자/커밋 메시지는 영어. 주석은 한국어 허용(기존 스타일 유지).

---

### Task 1: 프로젝트 스캐폴딩 + git 초기화

**Files:**
- Create: `poppy/__init__.py`
- Create: `ui/__init__.py`
- Create: `tests/__init__.py`
- Create: `.gitignore`
- Create: `motions/.gitkeep`

**Interfaces:**
- Consumes: 없음
- Produces: `poppy`, `ui`, `tests` 파이썬 패키지 디렉터리. `motions/` 저장 폴더.

- [ ] **Step 1: git 저장소 초기화**

이 폴더는 아직 git 저장소가 아니다.

```bash
cd "C:/Users/User/Desktop/자세 제작기"
git init
```

- [ ] **Step 2: 패키지 디렉터리와 빈 파일 생성**

`poppy/__init__.py`, `ui/__init__.py`, `tests/__init__.py` 를 빈 파일로 생성.

`.gitignore`:

```gitignore
__pycache__/
*.pyc
.pytest_cache/
```

`motions/.gitkeep` 는 빈 파일.

- [ ] **Step 3: pytest 동작 확인**

Run: `python -m pytest -q`
Expected: `no tests ran` (에러 없이 종료, exit code 5 허용)

- [ ] **Step 4: Commit**

```bash
git add .gitignore poppy ui tests motions
git commit -m "chore: scaffold package structure"
```

---

### Task 2: `poppy/protocol.py` — 순수 프로토콜 인코딩

기존 `poppy_arduino_python_code.py`의 `encode_command`/`make_packet`을 시리얼 의존 없는 순수 모듈로 이관한다.

**Files:**
- Create: `poppy/protocol.py`
- Test: `tests/test_protocol.py`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `LEG_LIST = ["LF", "RF", "LB", "RB"]`
  - `MAX_COMMAND_COUNT = 6`
  - `ACTION_MAP: dict[str, str]`
  - `encode_command(command: str, value=0) -> str` — 예: `("LF_ANGLE", 90) -> "LFA+0090"`
  - `make_packet(commands: list[tuple]) -> str` — 예: `-> "@01LFA+0090\n"` (끝에 `\n` 포함)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/test_protocol.py`:

```python
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
    assert protocol.encode_command("LB_ZERO") == "LBZ00000"


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
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `python -m pytest tests/test_protocol.py -q`
Expected: FAIL — `ModuleNotFoundError` 또는 `AttributeError`

- [ ] **Step 3: 구현**

`poppy/protocol.py`:

```python
LEG_LIST = ["LF", "RF", "LB", "RB"]
MAX_COMMAND_COUNT = 6

ACTION_MAP = {
    "UP": "U",
    "DOWN": "D",
    "ANGLE": "A",
    "SET": "A",
    "ROTATE": "R",
    "TURN": "R",
    "ZERO": "Z",
    "RESET": "Z",
}


def encode_command(command, value=0):
    """예: LF_ANGLE 90 -> LFA+0090, RF_DOWN 2 -> RFD00200, LF_ZERO -> LFZ00000"""
    command = command.upper().strip()

    if "_" not in command:
        raise ValueError(f"명령 형식 오류: {command}")

    leg, action_name = command.split("_", 1)

    if leg not in LEG_LIST:
        raise ValueError(f"다리 이름 오류: {leg}")

    if action_name not in ACTION_MAP:
        raise ValueError(f"동작 이름 오류: {action_name}")

    action = ACTION_MAP[action_name]

    if action == "Z":
        value_text = "00000"

    elif action in ("U", "D"):
        cm_100 = int(round(float(value) * 100))
        if cm_100 < 0 or cm_100 > 99999:
            raise ValueError("UP/DOWN 값은 0.00cm ~ 999.99cm 범위여야 합니다.")
        value_text = f"{cm_100:05d}"

    elif action in ("A", "R"):
        angle = int(round(float(value)))
        if angle < -9999 or angle > 9999:
            raise ValueError("각도는 -9999도 ~ 9999도 범위여야 합니다.")
        value_text = f"{angle:+05d}"

    else:
        raise ValueError(f"지원하지 않는 action: {action}")

    return f"{leg}{action}{value_text}"


def make_packet(commands):
    """commands: [(command, value), ...] -> "@NN....\\n" 문자열"""
    if len(commands) < 1:
        raise ValueError("명령은 최소 1개 이상이어야 합니다.")

    if len(commands) > MAX_COMMAND_COUNT:
        raise ValueError(f"명령은 한 번에 최대 {MAX_COMMAND_COUNT}개까지 가능합니다.")

    blocks = ""
    for item in commands:
        if len(item) == 1:
            command, value = item[0], 0
        else:
            command, value = item
        blocks += encode_command(command, value)

    return f"@{len(commands):02d}{blocks}\n"
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `python -m pytest tests/test_protocol.py -q`
Expected: PASS (14 passed)

- [ ] **Step 5: Commit**

```bash
git add poppy/protocol.py tests/test_protocol.py
git commit -m "feat: add pure protocol encoding module"
```

---

### Task 3: `poppy/controller.py` — 시리얼 연결/전송/응답

기존 `connect_arduino`/`send_many`/`wait_arduino_response`를 클래스로 이관한다. 테스트는 fake serial 객체를 주입해 응답 파싱을 검증한다.

**Files:**
- Create: `poppy/controller.py`
- Test: `tests/test_controller.py`

**Interfaces:**
- Consumes: `poppy.protocol.make_packet`
- Produces:
  - `class Controller`
    - `Controller.list_ports() -> list[str]` (staticmethod)
    - `connect(self, port: str, baud: int = 115200) -> None` (실패 시 예외)
    - `is_connected(self) -> bool`
    - `send_packet(self, packet: str, timeout_sec: float = 30) -> tuple[bool, list[str]]`
    - `send_commands(self, commands: list[tuple], timeout_sec: float = 30) -> tuple[bool, list[str]]`
    - `close(self) -> None`
  - 내부 속성 `self._serial` — 테스트에서 fake로 대체 가능.
  - `now()` 시간원은 `time.monotonic` 을 모듈 레벨 `_clock`로 두어 테스트에서 주입 가능하게 한다.

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/test_controller.py`:

```python
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
    ctrl.close()
    assert ctrl.is_connected() is False
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `python -m pytest tests/test_controller.py -q`
Expected: FAIL — `ImportError` / `AttributeError`

- [ ] **Step 3: 구현**

`poppy/controller.py`:

```python
import time

import serial
from serial.tools import list_ports as _list_ports

from poppy.protocol import make_packet

_clock = time.monotonic  # 테스트에서 대체 가능


class Controller:
    def __init__(self):
        self._serial = None

    @staticmethod
    def list_ports():
        return [p.device for p in _list_ports.comports()]

    def connect(self, port, baud=115200):
        ser = serial.Serial(port, baud, timeout=0.2)
        time.sleep(2)  # 아두이노 리셋 대기
        ser.reset_input_buffer()  # 부팅 시 READY 등 제거
        self._serial = ser

    def is_connected(self):
        return self._serial is not None

    def send_packet(self, packet, timeout_sec=30):
        if self._serial is None:
            raise RuntimeError("아두이노에 연결되지 않았습니다.")

        self._serial.write(packet.encode("ascii"))
        self._serial.flush()

        start = _clock()
        lines = []
        while _clock() - start < timeout_sec:
            if self._serial.in_waiting > 0:
                line = self._serial.readline().decode(errors="ignore").strip()
                if line:
                    lines.append(line)
                    if line == "OK":
                        return True, lines
                    if line.startswith("ERR"):
                        return False, lines
            time.sleep(0.01)

        return False, lines

    def send_commands(self, commands, timeout_sec=30):
        return self.send_packet(make_packet(commands), timeout_sec)

    def close(self):
        if self._serial is not None:
            self._serial.close()
            self._serial = None
```

주의: `test_send_packet_timeout`이 실제로 0.05초 안에 끝나려면 `time.sleep(0.01)` 반복이 문제 없다(응답 없을 때 monotonic 기준 루프 종료). 이 테스트는 실제 약 0.05초 걸린다 — 허용.

- [ ] **Step 4: 테스트 통과 확인**

Run: `python -m pytest tests/test_controller.py -q`
Expected: PASS (6 passed)

- [ ] **Step 5: Commit**

```bash
git add poppy/controller.py tests/test_controller.py
git commit -m "feat: add serial controller"
```

---

### Task 4: `poppy/motion.py` — 데이터 모델 + JSON + 각도 미러

**Files:**
- Create: `poppy/motion.py`
- Test: `tests/test_motion.py`

**Interfaces:**
- Consumes: `poppy.protocol.LEG_LIST`
- Produces:
  - `@dataclass Keyframe(angles: dict[str, int], hold_ms: int)`
  - `@dataclass Motion(name: str, keyframes: list[Keyframe])`
  - `keyframe_to_commands(kf: Keyframe) -> list[tuple]` — 예: `[("LF_ANGLE", 90), ("RF_ANGLE", -45), ("LB_ANGLE", 0), ("RB_ANGLE", 30)]` (LEG_LIST 순서)
  - `save_motion(motion: Motion, directory: str) -> str` (저장 경로 반환)
  - `load_motion(path: str) -> Motion`
  - `list_motions(directory: str) -> list[str]` (이름 목록, `.json` 제거, 정렬)
  - `class AngleMirror` — `angles: dict[str,int]`, `rotate(leg, delta)`, `set_angle(leg, value)`, `zero(leg)`, `snapshot() -> dict[str,int]`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/test_motion.py`:

```python
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
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `python -m pytest tests/test_motion.py -q`
Expected: FAIL — `ImportError`

- [ ] **Step 3: 구현**

`poppy/motion.py`:

```python
import json
import os
from dataclasses import dataclass, field

from poppy.protocol import LEG_LIST


@dataclass
class Keyframe:
    angles: dict
    hold_ms: int


@dataclass
class Motion:
    name: str
    keyframes: list = field(default_factory=list)


def keyframe_to_commands(kf):
    return [(f"{leg}_ANGLE", kf.angles[leg]) for leg in LEG_LIST]


def save_motion(motion, directory):
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, f"{motion.name}.json")
    data = {
        "name": motion.name,
        "keyframes": [
            {"angles": kf.angles, "hold_ms": kf.hold_ms}
            for kf in motion.keyframes
        ],
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


def load_motion(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    keyframes = [
        Keyframe(angles=kf["angles"], hold_ms=kf["hold_ms"])
        for kf in data["keyframes"]
    ]
    return Motion(name=data["name"], keyframes=keyframes)


def list_motions(directory):
    if not os.path.isdir(directory):
        return []
    names = [
        os.path.splitext(f)[0]
        for f in os.listdir(directory)
        if f.endswith(".json")
    ]
    return sorted(names)


class AngleMirror:
    def __init__(self):
        self.angles = {leg: 0 for leg in LEG_LIST}

    def rotate(self, leg, delta):
        self.angles[leg] += delta

    def set_angle(self, leg, value):
        self.angles[leg] = value

    def zero(self, leg):
        self.angles[leg] = 0

    def snapshot(self):
        return dict(self.angles)
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `python -m pytest tests/test_motion.py -q`
Expected: PASS (6 passed)

- [ ] **Step 5: 전체 테스트 확인 후 Commit**

Run: `python -m pytest -q`
Expected: PASS (26 passed)

```bash
git add poppy/motion.py tests/test_motion.py
git commit -m "feat: add motion data model with json and angle mirror"
```

---

### Task 5: `ui/worker.py` — 재생 QThread 워커

**Files:**
- Create: `ui/worker.py`
- Test: `tests/test_worker.py`

**Interfaces:**
- Consumes: `poppy.controller.Controller`, `poppy.motion.Motion`, `poppy.motion.keyframe_to_commands`
- Produces:
  - `class PlaybackWorker(QThread)`
    - `__init__(self, controller, motion)`
    - signals: `progress = Signal(int)` (완료된 키프레임 인덱스, 0-base), `log = Signal(str)`, `error = Signal(str)`, `done = Signal()`
    - `stop(self)` — 다음 키프레임 시작 전 안전 정지
    - `run(self)` — 각 키프레임: `controller.send_commands(...)` → 실패 시 `error` emit 후 종료 → 성공 시 `hold_ms` 대기 → `progress` emit. 끝나면 `done` emit.

- [ ] **Step 1: 실패하는 테스트 작성**

QThread지만 `run()`을 직접 호출해 동기 실행으로 검증한다(이벤트 루프 불필요).

`tests/test_worker.py`:

```python
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
```

`tests/conftest.py` 생성 (QApplication 픽스처 — Signal 사용에 필요):

```python
import pytest
from PySide6.QtWidgets import QApplication


@pytest.fixture(scope="session")
def qapp():
    app = QApplication.instance() or QApplication([])
    yield app
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `python -m pytest tests/test_worker.py -q`
Expected: FAIL — `ModuleNotFoundError: ui.worker`

- [ ] **Step 3: 구현**

`ui/worker.py`:

```python
import time

from PySide6.QtCore import QThread, Signal

from poppy.motion import keyframe_to_commands


class PlaybackWorker(QThread):
    progress = Signal(int)
    log = Signal(str)
    error = Signal(str)
    done = Signal()

    def __init__(self, controller, motion):
        super().__init__()
        self._controller = controller
        self._motion = motion
        self._stop = False

    def stop(self):
        self._stop = True

    def run(self):
        for i, kf in enumerate(self._motion.keyframes):
            if self._stop:
                self.log.emit("정지됨")
                break

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
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `python -m pytest tests/test_worker.py -q`
Expected: PASS (2 passed)

- [ ] **Step 5: Commit**

```bash
git add ui/worker.py tests/test_worker.py tests/conftest.py
git commit -m "feat: add playback worker thread"
```

---

### Task 6: `ui/app.py` — 메인 윈도우 골격 (연결 바 + 타임라인 + 라이브러리)

이 태스크는 실행 가능한 앱 골격을 만든다. 탭 내용(조그/편집)은 다음 태스크에서 채운다. 여기서는 빈 탭 2개 + 공용 상태(controller, 현재 Motion, AngleMirror) + 하단 타임라인 + 우측 라이브러리 패널 + 상단 연결 바를 만든다.

**Files:**
- Create: `ui/app.py`

**Interfaces:**
- Consumes: `poppy.controller.Controller`, `poppy.motion` 전체, `ui.worker.PlaybackWorker`
- Produces:
  - `class MainWindow(QMainWindow)` — 공용 상태 보유:
    - `self.controller: Controller`
    - `self.mirror: AngleMirror`
    - `self.motion: Motion` (편집 중 동작)
    - `self.timeline: QListWidget` (키프레임 목록)
    - `MOTIONS_DIR = "motions"`
    - 메서드: `add_keyframe(self, angles: dict, hold_ms: int)`, `refresh_timeline(self)`, `refresh_library(self)`, `selected_keyframe_index(self) -> int`
  - `main()` 진입점 — `python -m ui.app` 로 실행 가능.
  - 탭 위젯 `self.tabs`에 자리표시자 위젯 2개(`"라이브 조그"`, `"오프라인 편집"`) — 다음 태스크에서 교체.

- [ ] **Step 1: 구현**

`ui/app.py`:

```python
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

        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)

        root.addLayout(self._build_connection_bar())

        body = QHBoxLayout()
        root.addLayout(body, stretch=1)

        left = QVBoxLayout()
        body.addLayout(left, stretch=3)

        self.tabs = QTabWidget()
        # 자리표시자 — Task 7에서 실제 탭으로 교체
        self.tabs.addTab(QWidget(), "라이브 조그")
        self.tabs.addTab(QWidget(), "오프라인 편집")
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
            self.refresh_timeline()

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
        if not self.controller.is_connected():
            QMessageBox.warning(self, "경고", "먼저 아두이노에 연결하세요.")
            return
        if not self.motion.keyframes:
            return
        self._worker = PlaybackWorker(self.controller, self.motion)
        self._worker.log.connect(self._append_log)
        self._worker.error.connect(lambda s: self._append_log("ERROR: " + s))
        self._worker.progress.connect(lambda i: self.timeline.setCurrentRow(i))
        self._worker.start()

    def _stop(self):
        if self._worker is not None:
            self._worker.stop()

    def _append_log(self, text):
        self.log.appendPlainText(text)


def main():
    app = QApplication(sys.argv)
    win = MainWindow()
    win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: import 스모크 테스트**

Run: `python -c "import ui.app; print('import ok')"`
Expected: `import ok` (에러 없음)

- [ ] **Step 3: 앱 실행 수동 확인**

Run: `python -m ui.app`
확인:
- 창이 뜬다.
- 상단에 포트 드롭다운 + [연결] 버튼.
- 좌측 탭 2개(빈 화면), 하단 타임라인, 우측 라이브러리 패널.
- [새로] → 이름 입력 → [저장] 하면 `motions/<이름>.json` 생성되고 라이브러리 목록에 뜬다.
- 창을 닫는다.

- [ ] **Step 4: 저장 파일 확인**

Run: `python -c "import os; print(os.listdir('motions'))"`
Expected: 방금 저장한 `<이름>.json` 이 목록에 있음

- [ ] **Step 5: Commit**

```bash
git add ui/app.py
git commit -m "feat: add main window shell with connection bar, timeline, library"
```

---

### Task 7: 조그 탭 + 편집 탭 구현 및 연결

자리표시자 탭 2개를 실제 위젯으로 교체한다.

**Files:**
- Create: `ui/jog_tab.py`
- Create: `ui/edit_tab.py`
- Modify: `ui/app.py` (자리표시자 탭 → 실제 탭 교체)

**Interfaces:**
- Consumes: `MainWindow` (controller, mirror, add_keyframe, motion, refresh_timeline, selected_keyframe_index, _append_log)
- Produces:
  - `class JogTab(QWidget)` — `__init__(self, main_window)`. 다리별 조그 UI. 조그 시 `main.controller.send_commands([(f"{leg}_ROTATE", delta)])` 전송 + `main.mirror.rotate(leg, delta)` 갱신 + 표시. `[0점]` → `main.controller.send_commands([(f"{leg}_ZERO",)])` + `main.mirror.zero(leg)`. `[현재 자세 캡처]` → `main.add_keyframe(main.mirror.snapshot(), hold_ms)`.
  - `class EditTab(QWidget)` — `__init__(self, main_window)`. 선택된 키프레임의 4다리 각도 + hold_ms 편집. `[적용]` → 선택 키프레임 값 갱신 + `main.refresh_timeline()`.

- [ ] **Step 1: 조그 탭 구현**

`ui/jog_tab.py`:

```python
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
        ok, lines = self.main.controller.send_commands([(f"{leg}_ROTATE", delta)])
        for line in lines:
            self.main._append_log(line)
        if ok:
            self.main.mirror.rotate(leg, delta)
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
```

- [ ] **Step 2: 편집 탭 구현**

`ui/edit_tab.py`:

```python
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
```

- [ ] **Step 3: `ui/app.py`에서 자리표시자 탭 교체**

`ui/app.py` 상단 import에 추가:

```python
from ui.jog_tab import JogTab
from ui.edit_tab import EditTab
```

`__init__`의 아래 두 줄을

```python
        self.tabs.addTab(QWidget(), "라이브 조그")
        self.tabs.addTab(QWidget(), "오프라인 편집")
```

다음으로 교체:

```python
        self.tabs.addTab(JogTab(self), "라이브 조그")
        self.tabs.addTab(EditTab(self), "오프라인 편집")
```

- [ ] **Step 4: import 스모크 테스트**

Run: `python -c "import ui.app; print('import ok')"`
Expected: `import ok`

- [ ] **Step 5: 앱 실행 수동 확인 (하드웨어 없이 가능한 부분)**

Run: `python -m ui.app`
확인:
- "라이브 조그" 탭에 다리 4개 행 + 조그 버튼 + [현재 자세 캡처] 표시.
- 미연결 상태에서 조그 버튼 누르면 "먼저 아두이노에 연결하세요" 경고.
- [현재 자세 캡처] → 타임라인에 키프레임 추가됨(각도 전부 0).
- "오프라인 편집" 탭에서 타임라인 키프레임 선택 → [선택 불러오기] → 각도 수정 → [적용] → 타임라인 텍스트 갱신.
- [저장] → JSON 파일 생성.
- 창 닫기.

- [ ] **Step 6: 전체 테스트 + Commit**

Run: `python -m pytest -q`
Expected: PASS (28 passed)

```bash
git add ui/jog_tab.py ui/edit_tab.py ui/app.py
git commit -m "feat: add jog and edit tabs"
```

---

### Task 8: 하드웨어 연동 수동 검증 + README

**Files:**
- Create: `README.md`

**Interfaces:**
- Consumes: 완성된 앱
- Produces: 실행법 문서

- [ ] **Step 1: 하드웨어 연결 후 엔드투엔드 수동 검증**

아두이노 연결 상태에서:
- 앱 실행 → 포트 선택 → [연결] → "연결됨" 표시.
- 조그 탭에서 한 다리 [+ ►] → 실제 모터 회전 + 각도 표시 증가 + 로그에 `OK`.
- [0점(Z)] → 각도 0으로 리셋.
- 여러 다리 자세 맞춘 뒤 [현재 자세 캡처] 2~3회 → 타임라인에 키프레임들.
- [저장] → JSON 생성.
- [▶ 재생] → 각 키프레임 순서대로 모터 이동 + hold 시간 대기.
- [불러오기]로 저장한 동작 다시 로드 → 재생 재현 확인.

문제 발생 시 systematic-debugging 스킬로 대응.

- [ ] **Step 2: README 작성**

`README.md`:

```markdown
# 4족 로봇 모션 에디터

4개 스텝모터(다리)를 UI로 조작해 키프레임 동작을 만들고 JSON으로 저장/재생하는 툴.

## 요구사항

- Python 3.13
- PySide6, pyserial (`pip install PySide6 pyserial`)
- 아두이노에 `poppy_arduino_code.ino` 업로드

## 실행

```bash
python -m ui.app
```

## 사용법

1. 상단에서 COM 포트 선택 후 [연결].
2. **라이브 조그** 탭: 조그 버튼으로 각 다리 각도를 맞추고 [현재 자세 캡처]로 키프레임 추가.
3. **오프라인 편집** 탭: 타임라인에서 키프레임 선택 후 각도/유지시간 수정.
4. 우측 [저장]으로 `motions/<이름>.json` 저장, [불러오기]로 로드.
5. [▶ 재생]으로 동작 실행.

## 테스트

```bash
python -m pytest -q
```

## 구조

- `poppy/protocol.py` — 패킷 인코딩(순수 함수)
- `poppy/controller.py` — 시리얼 연결/전송
- `poppy/motion.py` — 키프레임/동작 데이터 + JSON
- `ui/` — PySide6 UI + 재생 워커
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add README"
```

---

## Self-Review 결과

**Spec 커버리지:**
- UI 플랫폼 PySide6 → Task 6,7 ✓
- 키프레임 절대각도 → Task 4 `keyframe_to_commands` (`_ANGLE`→`A`) ✓
- 탭 분리(조그/편집) → Task 7 ✓
- hold_ms 타이밍 → Task 4 데이터 + Task 5 워커 sleep ✓
- JSON + 라이브러리 패널 → Task 4 save/load + Task 6 라이브러리 ✓
- 각도 미러 모델 → Task 4 `AngleMirror` ✓
- QThread 재생 → Task 5 ✓
- 에러/타임아웃/미연결 처리 → Task 3 send_packet, Task 5 error emit, Task 6/7 연결 체크 ✓
- 기존 코드 분해 + `wait_arduino_response` 픽스 이관 → Task 2,3 ✓
- 테스트 전략 → Task 2,3,4,5 pytest, 6,7,8 수동 ✓

**플레이스홀더 스캔:** 없음. 모든 코드 스텝에 완전한 코드 포함.

**타입 일관성:** `Controller.send_commands`, `keyframe_to_commands`, `AngleMirror.snapshot`, `add_keyframe`, `refresh_timeline`, `selected_keyframe_index` 시그니처가 정의 태스크(2/3/4)와 소비 태스크(5/6/7) 간 일치 확인됨.
