import json
import os
from dataclasses import dataclass, field

from poppy.protocol import LEG_LIST


@dataclass
class Keyframe:
    angles: dict
    hold_ms: int


@dataclass
class Motion:
    name: str
    keyframes: list = field(default_factory=list)


def keyframe_to_commands(kf):
    return [(f"{leg}_ANGLE", kf.angles[leg]) for leg in LEG_LIST]


def save_motion(motion, directory):
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, f"{motion.name}.json")
    data = {
        "name": motion.name,
        "keyframes": [
            {"angles": kf.angles, "hold_ms": kf.hold_ms}
            for kf in motion.keyframes
        ],
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


def load_motion(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    keyframes = [
        Keyframe(angles=kf["angles"], hold_ms=kf["hold_ms"])
        for kf in data["keyframes"]
    ]
    return Motion(name=data["name"], keyframes=keyframes)


def list_motions(directory):
    if not os.path.isdir(directory):
        return []
    names = [
        os.path.splitext(f)[0]
        for f in os.listdir(directory)
        if f.endswith(".json")
    ]
    return sorted(names)


class AngleMirror:
    def __init__(self):
        self.angles = {leg: 0 for leg in LEG_LIST}

    def rotate(self, leg, delta):
        self.angles[leg] += delta

    def set_angle(self, leg, value):
        self.angles[leg] = value

    def zero(self, leg):
        self.angles[leg] = 0

    def snapshot(self):
        return dict(self.angles)
