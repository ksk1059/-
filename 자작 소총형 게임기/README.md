# 자작 소총형 게임기 (bang)

부스 홍보용 레일 슈터 게임에 쓰는 소총 모양 컨트롤러의 KiCad 프로젝트.

- MCU: ESP32-S3-WROOM-1 / 조준: BNO085 IMU
- 입력: 방아쇠, 탄창 탈착 장전(홀 센서), 노리쇠 당김
- 진동 모터(AO3400A MOSFET 구동)
- 화면: 총에 단 폰으로 PC 화면 무선 스트리밍

## 폴더
- `bang.kicad_pro / .kicad_sch / .kicad_pcb` — 프로젝트·회로도·PCB
- `lib/` — LCSC 부품 심볼·풋프린트·3D 모델
- `gerber/`, `jlc/` — JLCPCB 제작용 거버, BOM/CPL
- `stencil_home/` — 집 리플로우용 스텐실
- `drc/`, `erc/` — 검사 리포트
- `test_fw/` — 테스트 펌웨어(Arduino)
