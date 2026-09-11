import serial
import time

# -----------------------------------------------------------------------------
# 기본 설정
# -----------------------------------------------------------------------------

PORT = "COM5"
BAUD_RATE = 115200
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

LEG_LIST = ["LF", "RF", "LB", "RB"]

# -----------------------------------------------------------------------------
# 아두이노 연결
# -----------------------------------------------------------------------------

def connect_arduino():
    try:
        arduino = serial.Serial(PORT, BAUD_RATE, timeout=0.2)
        time.sleep(2)

        # 아두이노 리셋 직후 출력되는 READY 같은 메시지 제거
        arduino.reset_input_buffer()

        print(f"Arduino connected: {PORT}, {BAUD_RATE}")
        return arduino

    except serial.SerialException as e:
        print("아두이노 연결 실패")
        print("확인할 것:")
        print("1. COM 포트가 COM5가 맞는지 확인")
        print("2. 아두이노 IDE의 시리얼 모니터가 닫혀 있는지 확인")
        print("3. USB 케이블 연결 확인")
        raise e

arduino = connect_arduino()

# -----------------------------------------------------------------------------
# 명령어 인코딩
# -----------------------------------------------------------------------------

def encode_command(command, value=0):
    """
    입력 예:
    LF_UP 3       -> LFU00300
    RF_DOWN 2     -> RFD00200
    LB_ROTATE 90  -> LBR+0090
    RB_ANGLE 360  -> RBA+0360
    LF_ZERO       -> LFZ00000
    """

    command = command.upper().strip()

    if "_" not in command:
        raise ValueError(f"명령 형식 오류: {command}")

    leg, action_name = command.split("_", 1)

    if leg not in LEG_LIST:
        raise ValueError(f"다리 이름 오류: {leg}")

    if action_name not in ACTION_MAP:
        raise ValueError(f"동작 이름 오류: {action_name}")

    action = ACTION_MAP[action_name]

    # Z 명령은 현재 위치를 0으로 잡는 명령이라 값은 항상 00000
    if action == "Z":
        value_text = "00000"

    elif action in ["U", "D"]:
        # cm 단위 입력
        # 3cm -> 00300
        # 0.9cm -> 00090
        cm_100 = int(round(float(value) * 100))

        if cm_100 < 0 or cm_100 > 99999:
            raise ValueError("UP/DOWN 값은 0.00cm ~ 999.99cm 범위여야 합니다.")

        value_text = f"{cm_100:05d}"

    elif action in ["A", "R"]:
        # 각도 단위 입력
        # 90도 -> +0090
        # -90도 -> -0090
        angle = int(round(float(value)))

        if angle < -9999 or angle > 9999:
            raise ValueError("각도는 -9999도 ~ 9999도 범위여야 합니다.")

        value_text = f"{angle:+05d}"

    else:
        raise ValueError(f"지원하지 않는 action: {action}")

    return f"{leg}{action}{value_text}"

# -----------------------------------------------------------------------------
# 패킷 생성
# -----------------------------------------------------------------------------

def make_packet(commands):
    """
    commands 예:
    [
        ("LF_UP", 3),
        ("RB_UP", 3),
        ("RF_DOWN", 2),
        ("LB_DOWN", 2),
    ]

    결과:
    @04LFU00300RBU00300RFD00200LBD00200
    """

    if len(commands) < 1:
        raise ValueError("명령은 최소 1개 이상이어야 합니다.")

    if len(commands) > MAX_COMMAND_COUNT:
        raise ValueError("명령은 한 번에 최대 6개까지 가능합니다.")

    blocks = ""

    for item in commands:
        if len(item) == 1:
            command = item[0]
            value = 0
        else:
            command, value = item

        blocks += encode_command(command, value)

    packet = f"@{len(commands):02d}{blocks}\n"
    return packet

# -----------------------------------------------------------------------------
# 아두이노 응답 대기
# -----------------------------------------------------------------------------

def wait_arduino_response(timeout_sec=30):
    start_time = time.time()
    response_lines = []

    while time.time() - start_time < timeout_sec:
        if arduino.in_waiting > 0:
            line = arduino.readline().decode(errors="ignore").strip()

            if line:
                response_lines.append(line)
                print("ARDUINO:", line)

                if line == "OK":
                    return True, response_lines

                if line.startswith("ERR"):
                    return False, response_lines

        time.sleep(0.01)

    print("ARDUINO: 응답 시간 초과")
    return False, response_lines

# -----------------------------------------------------------------------------
# 명령 전송
# -----------------------------------------------------------------------------

def send_many(commands, timeout_sec=30):
    packet = make_packet(commands)

    print("SEND:", packet.strip())

    arduino.write(packet.encode("ascii"))
    arduino.flush()

    ok, lines = wait_arduino_response(timeout_sec)

    if not ok:
        print("명령 처리 실패 또는 시간 초과")

    return ok, lines


def send_command(command, value=0, timeout_sec=30):
    return send_many([(command, value)], timeout_sec)

# -----------------------------------------------------------------------------
# 테스트 예시
# -----------------------------------------------------------------------------

# LF 다리를 360도 상대 회전
# send_command("LF_ROTATE", 360)

# LF 다리를 3cm 올리기
# send_command("LF_UP", 3)

# 현재 LF 위치를 0으로 리셋
# send_command("LF_ZERO")

# 여러 다리 동시에 이동
# send_many([
#     ("LF_UP", 3),
#     ("RB_UP", 3),
#     ("RF_DOWN", 2),
#     ("LB_DOWN", 2),
# ])

# 각 다리 360도 회전 테스트
# send_many([
#     ("LF_ROTATE", 360),
#     ("RF_ROTATE", 360),
#     ("LB_ROTATE", 360),
#     ("RB_ROTATE", 360),
# ])
