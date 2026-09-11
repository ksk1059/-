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

    def connect(self, port, baud=9600):
        if self._serial is not None:
            self.close()
        ser = serial.Serial(port, baud, timeout=0.2)
        time.sleep(2)  # 아두이노 리셋 대기
        ser.reset_input_buffer()  # 부팅 시 START 등 제거
        self._serial = ser

    def is_connected(self):
        return self._serial is not None

    def send_packet(self, packet, timeout_sec=30):
        if self._serial is None:
            raise RuntimeError("아두이노에 연결되지 않았습니다.")

        self._serial.reset_input_buffer()  # 이전 명령의 잔여 바이트 제거
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
