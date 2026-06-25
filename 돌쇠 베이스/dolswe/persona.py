# 정적 페르소나 제거 — 캐릭터는 memory(누적 대화)에서 창발.
# 이 모듈은 카메라 상황을 한 줄로 만드는 헬퍼만 남김.

def build_context_line(snapshot: dict) -> str:
    parts = []
    pc = snapshot.get("people_count") or 0
    if pc >= 1:
        parts.append(f"앞에 사람 {pc}명")
    if snapshot.get("color"):
        parts.append(f"{snapshot['color']} 옷")
    if snapshot.get("percept"):  # 사용자가 가르쳐 인식된 손짓/사물
        parts.append(f"인식: {snapshot['percept']}")
    if snapshot.get("scene"):
        parts.append(f"장면: {snapshot['scene']}")
    if not parts:
        return ""
    return "[지금 보이는 것] " + ", ".join(parts)
