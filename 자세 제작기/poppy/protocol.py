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
    """예: LF_ANGLE 90 -> LFA+0090, RF_DOWN 2 -> RFD00200, LF_ZERO -> LFRE00000

    리셋(ZERO/RESET)은 펌웨어 규격에 맞춰 RE 명령블록(다리2 + RE + 00000)으로 만든다.
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

    if action == "Z":
        return f"{leg}RE00000"

    if action in ("U", "D"):
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
