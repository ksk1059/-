"""보드레이트 스캔. 앱 닫고 실행. Arduino 포트로 여러 보드레이트를 시도한다.

사용:
    python baud_scan.py COM7

각 보드레이트마다: open -> 리셋 대기 -> 부팅 수신 바이트를 raw 로 찍고,
읽힌 데이터에 사람이 읽을 수 있는 ASCII(예: READY)가 있는지 표시한다.
정상 보드레이트에서는 'READY' 가 깨끗하게 보여야 한다.
"""
import sys
import time

import serial

BAUDS = [9600, 19200, 38400, 57600, 74880, 115200, 250000]


def printable_ratio(data):
    if not data:
        return 0.0
    ok = sum(1 for b in data if 32 <= b <= 126 or b in (10, 13))
    return ok / len(data)


def main():
    if len(sys.argv) < 2:
        print("사용: python baud_scan.py COM7")
        return
    port = sys.argv[1]

    for baud in BAUDS:
        print(f"\n=== {baud} baud ===")
        try:
            ser = serial.Serial(port, baud, timeout=1)
        except Exception as e:
            print(f"  open 실패: {type(e).__name__}: {e}")
            continue
        try:
            time.sleep(2.2)  # 리셋 후 부팅
            data = ser.read(ser.in_waiting or 1)
            time.sleep(0.3)
            data += ser.read(ser.in_waiting or 0)
            ratio = printable_ratio(data)
            sample = data[:60]
            print(f"  수신 {len(data)}바이트, 출력가능비율={ratio:.0%}")
            print(f"  raw : {sample!r}")
            try:
                print(f"  텍스트: {data.decode(errors='replace').strip()[:80]!r}")
            except Exception:
                pass
            if b"READY" in data:
                print("  >>> 여기다! 'READY' 발견 — 이 보드레이트가 맞음")
        finally:
            ser.close()

    print("\n스캔 끝. 'READY'가 뜬 보드레이트가 펌웨어 실제 값이다.")
    print("어디서도 READY가 안 뜨면 → poppy 스케치가 안 올라간 것(Arduino IDE로 업로드 필요).")


if __name__ == "__main__":
    main()
