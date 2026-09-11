# 4족 로봇 모션 에디터

4개 스텝모터(다리)를 UI로 조작해 키프레임 동작을 만들고 JSON으로 저장/재생하는 데스크톱 툴.
PC(Python/PySide6) → USB 시리얼(9600) → Arduino Mega → 스텝모터 4개.

## 요구사항

- Python 3.10+ (개발 환경 3.13)
- 의존성 설치:

```bash
pip install -r requirements.txt
```

- Arduino Mega에 `robot_firmware/robot_firmware.ino` 업로드 (Arduino IDE, Board: Arduino Mega 2560)

## 실행

```bash
python run.py
```

`run.py`가 작업 경로를 프로젝트 루트로 고정하므로 어느 위치에서 실행해도 된다.

## 사용법

1. 상단에서 COM 포트 선택 후 **[연결]**.
2. **라이브 조그** 탭: 조그 버튼으로 각 다리 각도를 맞추고 **[현재 자세 캡처]**로 키프레임 추가.
3. **오프라인 편집** 탭: 타임라인에서 키프레임 선택 → **[선택 불러오기]** → 각도/유지시간 수정 → **[적용]** (모터 안 움직임).
4. 우측 **[새로]**로 동작 생성(라이브러리에 바로 추가), **[저장]**으로 갱신, **[불러오기]**로 로드.
5. **[▶ 재생]**으로 동작 실행.

## 하드웨어 보정

`robot_firmware/robot_firmware.ino` 상단 상수를 조립에 맞게 수정한다.

- `STEPS_PER_360` — 360도 입력 시 움직일 스텝 수.
- `motor_dir[]` — 다리별 회전 방향(+1/-1). 반대로 돌면 부호를 바꾼다.
- `motor_pins[][]` — 다리별 모터 핀 배선(IN1~IN4).
- `BACKLASH_DIV` — 방향 전환 시 백래시 보정량.

## 프로토콜 요약

보드레이트 **9600**. 부팅 시 `START` 출력.

**패킷 명령** (앱이 사용): `@` + 명령개수(2자리) + 명령블록들 + `\n`

| 명령블록 | 형식 | 뜻 | 예 |
|---|---|---|---|
| A | 다리2 + `A` + 각도5 (8글자) | SET 기준 절대각도 이동 | `LFA+0090` = LF를 +90° |
| RE | 다리2 + `RE` + `00000` (9글자) | 해당 다리 위치값 0으로 리셋 | `LFRE00000` |

예: `@04LFA+0090RFA-0045LBA+0000RBA+0030` → 4다리 동시 절대각도 이동.

응답: `OK`(성공), `ERR: ...`(실패), `RESET LF`(RE), `START`(부팅).

**단순 명령** (펌웨어가 별도 지원, 앱 미사용): `1:360`(모터1 +360°), `F:180`, `B:-180`, `ALL:90`, `SET`(현재를 0으로 저장), `RESET`/`RE`(SET 위치로 복귀), `POS`.

## 구조

- `run.py` — 실행 진입점
- `poppy/protocol.py` — 패킷 인코딩(순수 함수)
- `poppy/controller.py` — 시리얼 연결/전송(9600)
- `poppy/motion.py` — 키프레임/동작 데이터 + JSON
- `ui/` — PySide6 UI + 재생 워커
- `robot_firmware/robot_firmware.ino` — Arduino 펌웨어(9600, @ 패킷 + 단순명령)

## 진단 도구

시리얼 문제 시 (앱 닫고 실행):

- `python serial_diag.py COM7` — 포트 열고 `@01LFRE00000` 보내 응답 확인(정상이면 `RESET LF`+`OK`).
- `python baud_scan.py COM7` — 여러 보드레이트로 펌웨어 부팅메시지 스캔.
