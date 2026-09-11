"""시리얼 진단. 앱을 완전히 닫은 상태에서 실행하라.

사용:
    python serial_diag.py COM5
포트 생략 시 감지된 포트 목록만 출력.

open -> reset -> write 테스트 패킷 -> read 응답 을 단계별로 찍고,
실패하면 예외 타입/메시지를 그대로 출력한다.
"""
import sys
import time
import traceback

import serial
from serial.tools import list_ports

BAUD = 9600
# 안전한 테스트 패킷: LF 다리 위치값 0으로 리셋(모터 안 움직임)
# 실제 펌웨어 형식: RE 명령블록
TEST_PACKET = b"@01LFRE00000\n"


def main():
    print("=== 감지된 포트 ===")
    ports = list(list_ports.comports())
    if not ports:
        print("(없음) - USB 케이블/드라이버 확인")
    for p in ports:
        print(f"  {p.device}  |  {p.description}  |  hwid={p.hwid}")

    if len(sys.argv) < 2:
        print("\n포트 인자를 주면 open/write/read 테스트를 한다: python serial_diag.py COM5")
        return

    port = sys.argv[1]
    print(f"\n=== 1) open {port} @ {BAUD} ===")
    try:
        ser = serial.Serial(port, BAUD, timeout=1)
    except Exception as e:
        print(f"OPEN 실패: {type(e).__name__}: {e}")
        print("→ 포트를 다른 프로그램(Arduino IDE 시리얼 모니터, 다른 파이썬)이 잡고 있거나,")
        print("  포트 번호가 틀렸거나, 드라이버 문제.")
        return
    print("OPEN OK")

    try:
        print("\n=== 2) 부팅 대기 2초 + 입력버퍼 비우기 ===")
        time.sleep(2)
        boot = ser.read(ser.in_waiting or 0)
        print(f"부팅 중 수신 바이트: {boot!r}")
        ser.reset_input_buffer()

        print("\n=== 3) write 테스트 패킷 ===")
        print(f"보냄: {TEST_PACKET!r}")
        ser.write(TEST_PACKET)
        ser.flush()
        print("WRITE OK")

        print("\n=== 4) 응답 read (최대 3초) ===")
        start = time.monotonic()
        got = []
        while time.monotonic() - start < 3:
            if ser.in_waiting > 0:
                line = ser.readline().decode(errors="ignore").strip()
                if line:
                    print(f"  수신: {line!r}")
                    got.append(line)
                    if line == "OK" or line.startswith("ERR"):
                        break
            time.sleep(0.01)
        if not got:
            print("  (응답 없음) → 펌웨어 미업로드 / 보드레이트 불일치 / 배선 문제 가능")
    except Exception as e:
        print(f"\n통신 중 예외 발생: {type(e).__name__}: {e}")
        traceback.print_exc()
    finally:
        ser.close()
        print("\n=== 포트 닫음. 진단 끝 ===")


if __name__ == "__main__":
    main()
