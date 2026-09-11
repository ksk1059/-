# 4족 로봇 모션 에디터 — 설계 문서

작성일: 2026-07-09

## 목적

UI로 4개 스텝모터(다리)를 조작하고, 자세(키프레임)를 모아 동작(motion)을 만든 뒤
JSON으로 저장/재생하는 전용 데스크톱 툴. 기존 `poppy_arduino_code.ino`의 패킷
프로토콜과 `poppy_arduino_python_code.py`의 시리얼 로직을 재사용한다.

## 확정된 요구사항

- **UI 플랫폼**: Python PyQt/PySide 데스크톱 앱.
- **동작 정의**: 키프레임 시퀀스. 각 키프레임은 4다리 절대각도. 재생 시 실제 아두이노 패킷 전송.
- **키프레임 값**: 절대각도(`A` 명령) 기반. 시작위치 무관하게 동일 자세 재현.
- **조작 방식**: 탭 분리 — 라이브 조그(실제 모터 구동+캡처) 탭 / 오프라인 숫자편집 탭. 둘 다 같은 키프레임 리스트에 반영.
- **재생 타이밍**: 키프레임별 유지시간(`hold_ms`). 전체 반복 루프 없음.
- **저장**: 동작마다 JSON 파일 + UI 라이브러리 패널(목록/로드/삭제/이름변경).

## 아키텍처

기존 단일 `.py`를 재사용 가능한 라이브러리로 분리하고 그 위에 UI를 올린다.

```
자세 제작기/
├─ poppy_arduino_code.ino        # 아두이노 펌웨어 (변경 없음)
├─ poppy/
│  ├─ __init__.py
│  ├─ protocol.py     # encode_command, make_packet — 순수 함수, 시리얼 의존 없음
│  ├─ controller.py   # 시리얼 연결/전송/응답대기 (기존 connect/send_many/wait_response)
│  └─ motion.py       # Keyframe/Motion 데이터 + JSON load/save + 키프레임→패킷 변환
├─ ui/
│  ├─ app.py          # PyQt 메인 윈도우, 탭 구성, 진입점
│  ├─ jog_tab.py      # 라이브 조그 + 캡처 탭
│  ├─ edit_tab.py     # 오프라인 숫자 편집 탭
│  ├─ library_panel.py# 저장 동작 목록 패널
│  └─ worker.py       # QThread 워커: 시리얼 송수신 (UI 프리즈 방지)
├─ motions/           # 저장된 *.json 동작 파일
└─ tests/
   ├─ test_protocol.py
   └─ test_motion.py
```

### 모듈 책임과 경계

| 모듈 | 하는 일 | 의존성 | 인터페이스 |
|---|---|---|---|
| `protocol.py` | 명령/패킷 문자열 인코딩 | 없음 (순수) | `encode_command(cmd, value) -> str`, `make_packet(commands) -> str` |
| `controller.py` | 시리얼 연결·전송·응답 파싱 | pyserial, protocol | `connect()`, `send_packet(str) -> (ok, lines)`, `close()`, `list_ports()` |
| `motion.py` | 데이터 모델, JSON, 각도 미러 | protocol | `Keyframe`, `Motion`, `save/load`, `keyframe_to_packet(kf) -> str` |
| `worker.py` | 재생 스레드 | controller, motion, Qt | 시그널: `progress`, `log`, `finished`, `error` |
| `ui/*` | 화면·입력 | 위 전부 | — |

`protocol.py`는 하드웨어 없이 문자열만 다뤄 단위테스트가 쉽다. UI는 `controller`/`motion`만
호출하고 프로토콜 세부는 모른다.

## 데이터 모델

```python
# motion.py (dataclass 기반)

Keyframe:
    angles: dict[str, int]   # {"LF": 90, "RF": -45, "LB": 0, "RB": 30}, 절대각도(도)
    hold_ms: int             # 자세 도달 후 유지시간

Motion:
    name: str
    keyframes: list[Keyframe]
```

JSON 파일 형식 (`motions/<name>.json`):

```json
{
  "name": "walk_forward",
  "keyframes": [
    { "angles": {"LF": 90, "RF": -45, "LB": 0, "RB": 30}, "hold_ms": 500 },
    { "angles": {"LF": 0,  "RF": 0,   "LB": 0, "RB": 0},  "hold_ms": 300 }
  ]
}
```

### 각도 미러 모델

아두이노의 `now_step`(현재 절대위치)을 Python이 직접 읽을 수 없으므로, Python이 각 다리의
현재 각도 모델을 자체 보유한다.

- 조그(`R` 상대회전) 전송 시: 미러 각도 += 회전량.
- 절대설정(`A`) 전송 시: 미러 각도 = 목표값.
- 0점(`Z`) 전송 시: 미러 각도 = 0.
- "현재 자세 캡처" 시: 4다리 미러 각도를 새 키프레임으로 저장.

주의: 미러는 명령 기반 추정이라 물리적 미끄러짐/탈조는 반영 못 한다. `Z`로 재정렬 가능.

### 키프레임 → 패킷 변환 / 재생

각 키프레임은 4다리를 `A`(절대각도) 명령 한 패킷으로 변환한다.

```
Keyframe{LF:90, RF:-45, LB:0, RB:30}
  -> make_packet([("LF_ANGLE",90),("RF_ANGLE",-45),("LB_ANGLE",0),("RB_ANGLE",30)])
  -> "@04LFA+0090RFA-0045LBA+0000RBA+0030\n"
```

재생 루프: 각 키프레임 패킷 전송 → `OK` 대기 → `hold_ms` sleep → 다음 키프레임.

## UI 레이아웃

**상단 공통 바**: COM 포트 드롭다운 + [연결]/[해제] 버튼 + 상태/로그 표시.

**탭 A — 라이브 조그**
- 다리 4개 각각: `[◄ -]` / 현재각도 / `[+ ►]` 조그 버튼(`R` 상대회전 실전송), `[0점(Z)]` 버튼.
- 조그 스텝(도) 조절 필드.
- `[현재 자세 캡처]` → 4다리 미러 각도를 새 키프레임으로 타임라인에 추가.

**탭 B — 오프라인 편집**
- 선택된 키프레임의 4다리 각도 숫자 입력 + `hold_ms` 입력.
- 모터를 움직이지 않고 값만 편집.

**하단 공통 — 타임라인**: 키프레임 리스트(선택/순서변경/추가/삭제/복제), `[▶ 재생]` `[■ 정지]`.

**우측 — 라이브러리 패널**: `motions/` 목록, `[새로]` `[저장]` `[불러오기]` `[삭제]` `[이름변경]`.

## 재생 엔진 & 에러 처리

- 재생은 QThread 워커에서 실행 → GUI 프리즈 방지. 진행/로그를 Qt 시그널로 UI에 전달.
- 각 키프레임 전송 후 `OK` 대기. `ERR...` 수신 시 재생 중단 + 로그 표시.
- 응답 타임아웃(기본 30초) 초과 시 중단.
- `[정지]`: 현재 키프레임 완료 후 안전 정지(모터 중간 강제차단 안 함).
- 미연결 상태에서 재생/조그 시도 → 해당 버튼 비활성 + 경고 메시지.
- 시리얼 연결 실패/포트 점유 → 사용자에게 메시지(기존 안내 문구 재사용).

## 테스트 전략

- `test_protocol.py`: 순수 함수 단위테스트.
  - `encode_command("LF_ANGLE", 90) == "LFA+0090"`
  - `encode_command("RF_DOWN", 2) == "RFD00200"`
  - `encode_command("LB_ZERO") == "LBZ00000"`
  - `make_packet([...])` 헤더/개수/길이 검증.
  - 범위 초과·잘못된 다리/동작 이름 예외 검증.
- `test_motion.py`: JSON 저장→로드 라운드트립, `keyframe_to_packet` 변환 정확성, 미러 각도 갱신 로직.
- `controller.py`: fake serial 목으로 응답 파싱(`OK`/`ERR`/타임아웃) 일부 자동화. 실제 하드웨어 연동은 수동 검증.
- UI: 수동 검증(조그→캡처→저장→재생 라운드트립 시나리오).

## 범위 밖 (YAGNI)

- 전체 동작 반복 루프/횟수 옵션.
- 키프레임 사이 각도 보간(중간 프레임 자동 생성).
- cm 단위(`U`/`D`) 높이를 키프레임에 저장 — 키프레임은 각도만.
- 다중 로봇/다중 포트 동시 제어.
- 3D 시각화.

## 기존 코드 처리

- `poppy_arduino_python_code.py`는 `poppy/protocol.py` + `poppy/controller.py`로 분해.
  이미 수정한 `wait_arduino_response` 분리 버그픽스를 `controller.py`로 이관.
- `.ino` 펌웨어는 변경하지 않는다(프로토콜 그대로 사용).
