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
