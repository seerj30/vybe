"""Emotes (spec §9 emote slot) and the idle loop: keyframes on the shared rig.

Rotations are Euler degrees in avatar space (X = the avatar's left, Y = back, Z = up), applied in the
parent bone's posed frame. Useful signs: arm forward = X negative; left arm out and up = Y negative
(right arm: Y positive); bend the body forward = X positive. Offsets are meters in avatar space.
"""

from __future__ import annotations

from typing import Dict, List, Sequence, Tuple

Key = Tuple[int, Dict[str, Sequence[float]], Dict[str, Sequence[float]]]

REST: Key = (0, {}, {})


def _both(bone: str, x: float = 0, y: float = 0, z: float = 0) -> Dict[str, Sequence[float]]:
    """The same motion on both sides (mirrored Y and Z)."""
    return {f"{bone}_L": (x, y, z), f"{bone}_R": (x, -y, -z)}


def _merge(*parts: Dict[str, Sequence[float]]) -> Dict[str, Sequence[float]]:
    out: Dict[str, Sequence[float]] = {}
    for p in parts:
        out.update(p)
    return out


def idle() -> List[Key]:
    return [
        (0, {}, {}),
        (45, _merge({"chest": (-2.5, 0, 0), "head": (1.5, 0, 1)}, _both("upper_arm", 0, -2.5, 0)), {"hips": (0, 0, 0.004)}),
        (90, {}, {}),
    ]


def swish() -> List[Key]:
    crouch_legs = _merge(_both("upper_leg", -32), _both("lower_leg", 55), _both("foot", -22))
    return [
        REST,
        (10, _merge(crouch_legs, _both("upper_arm", -75, -8), _both("lower_arm", -70), {"chest": (6, 0, 0)}),
         {"hips": (0, 0, -0.06)}),
        (20, _merge(_both("upper_arm", -165, -10), _both("lower_arm", -25), _both("hand", -35), {"head": (-12, 0, 0)}),
         {"root": (0, 0, 0.13)}),
        (27, _merge(_both("upper_arm", -160, -10), _both("lower_arm", -10), _both("hand", 60), {"head": (-10, 0, 0)}),
         {"root": (0, 0, 0.06)}),
        (36, _merge(crouch_legs, _both("upper_arm", -60, -10), _both("hand", 40)), {"hips": (0, 0, -0.05)}),
        (48, {}, {}),
    ]


def wave() -> List[Key]:
    up = {"upper_arm_R": (0, 140, 0), "head": (0, -8, 0)}
    return [
        REST,
        (8, _merge(up, {"lower_arm_R": (0, 10, 0)}), {}),
        (15, _merge(up, {"lower_arm_R": (0, 40, 0), "hand_R": (0, 10, 0)}), {}),
        (22, _merge(up, {"lower_arm_R": (0, -15, 0), "hand_R": (0, -10, 0)}), {}),
        (29, _merge(up, {"lower_arm_R": (0, 40, 0), "hand_R": (0, 10, 0)}), {}),
        (36, _merge(up, {"lower_arm_R": (0, 0, 0)}), {}),
        (46, {}, {}),
    ]


def cheer() -> List[Key]:
    arms = _merge(_both("upper_arm", 0, -150, 0), _both("lower_arm", 0, -15, 0), {"chest": (-8, 0, 0), "head": (-10, 0, 0)})
    bend = _merge(_both("upper_leg", -20), _both("lower_leg", 35), _both("foot", -15))
    return [
        REST,
        (7, _merge(arms, bend), {"hips": (0, 0, -0.04)}),
        (13, arms, {"root": (0, 0, 0.09)}),
        (19, _merge(arms, bend), {"hips": (0, 0, -0.04)}),
        (25, arms, {"root": (0, 0, 0.09)}),
        (32, _merge(arms, bend), {"hips": (0, 0, -0.03)}),
        (42, {}, {}),
    ]


def groove() -> List[Key]:
    keys: List[Key] = [REST]
    arms = _both("lower_arm", -85)
    for i, f in enumerate((10, 20, 30, 40, 50)):
        s = 1 if i % 2 == 0 else -1
        rot = _merge(
            arms,
            {"hips": (0, 6 * s, 12 * s), "chest": (0, -5 * s, -10 * s), "head": (8, 0, 6 * s)},
            {"upper_arm_L": (-25 * s, -12, 0), "upper_arm_R": (25 * s, 12, 0)},
            {"upper_leg_L": (-18 if s > 0 else 0, 0, 0), "lower_leg_L": (30 if s > 0 else 0, 0, 0),
             "upper_leg_R": (-18 if s < 0 else 0, 0, 0), "lower_leg_R": (30 if s < 0 else 0, 0, 0)},
        )
        keys.append((f, rot, {"hips": (0.03 * s, 0, -0.025)}))
    keys.append((62, {}, {}))
    return keys


def bow() -> List[Key]:
    down = _merge(
        {"spine": (18, 0, 0), "chest": (22, 0, 0), "head": (12, 0, 0)},
        {"upper_arm_R": (-35, 20, -20), "lower_arm_R": (-95, 0, 0)},
        {"upper_arm_L": (18, 0, 0), "lower_arm_L": (-10, 0, 0)},
    )
    return [REST, (14, down, {}), (32, down, {}), (46, {}, {})]


EMOTES = {"swish": swish, "wave": wave, "cheer": cheer, "groove": groove, "bow": bow}
# The frame shown in the thumbnail of each emote.
PEAK = {"swish": 20, "wave": 15, "cheer": 13, "groove": 20, "bow": 20}
