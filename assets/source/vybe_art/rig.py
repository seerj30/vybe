"""The one authoritative VYBE rig (spec §9): bone names, bind pose, body regions and body preset sizes.

Change the bone layout only together with a new RIG_VERSION: every item states the rig version it was
made for, and the checker refuses a mismatch.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from typing import Dict, List, Optional, Tuple

from .geom import Vec

RIG_VERSION = "vybe-rig-1"


@dataclass(frozen=True)
class Bone:
    name: str
    head: Vec
    tail: Vec
    parent: Optional[str]


def _sided(name: str, head: Vec, tail: Vec, parent: str) -> List[Bone]:
    """A left bone (+X, the avatar's left) and its mirror."""
    mirror_parent = parent.replace("_L", "_R")
    return [
        Bone(f"{name}_L", head, tail, parent),
        Bone(f"{name}_R", (-head[0], head[1], head[2]), (-tail[0], tail[1], tail[2]), mirror_parent),
    ]


# Bind pose: relaxed A-pose, arms 18 degrees from the body. Heights in meters; the avatar is 1.56 m tall.
SHOULDER: Vec = (0.162, 0.0, 1.065)
ELBOW: Vec = (0.23, 0.0, 0.86)
WRIST: Vec = (0.28, 0.0, 0.67)
HIP: Vec = (0.08, 0.0, 0.77)
KNEE: Vec = (0.085, 0.0, 0.43)
ANKLE: Vec = (0.085, 0.0, 0.10)

BONES: List[Bone] = [
    Bone("root", (0, 0, 0), (0, 0, 0.12), None),
    Bone("hips", (0, 0, 0.77), (0, 0, 0.86), "root"),
    Bone("spine", (0, 0, 0.86), (0, 0, 0.98), "hips"),
    Bone("chest", (0, 0, 0.98), (0, 0, 1.12), "spine"),
    Bone("neck", (0, 0, 1.12), (0, 0, 1.19), "chest"),
    Bone("head", (0, 0, 1.19), (0, 0, 1.56), "neck"),
    *_sided("shoulder", (0.03, 0, 1.065), (0.155, 0, 1.065), "chest"),
    *_sided("upper_arm", SHOULDER, ELBOW, "shoulder_L"),
    *_sided("lower_arm", ELBOW, WRIST, "upper_arm_L"),
    *_sided("hand", WRIST, (0.30, 0, 0.59), "lower_arm_L"),
    *_sided("upper_leg", HIP, KNEE, "hips"),
    *_sided("lower_leg", KNEE, ANKLE, "upper_leg_L"),
    *_sided("foot", ANKLE, (0.085, -0.11, 0.03), "lower_leg_L"),
]
BONE_NAMES = [b.name for b in BONES]

# Body regions (spec §9 coverage masks). Each region is one mesh of the body; an item hides a region with
# hideBodyRegions. "hair" is not a body mesh: it hides the preset hair and the hair slot (hats).
REGIONS: Dict[str, List[str]] = {
    "head": ["head"],
    "neck": ["neck"],
    "torso": ["spine", "chest", "shoulder_L", "shoulder_R"],
    "hips": ["hips"],
    "upper_arms": ["upper_arm_L", "upper_arm_R"],
    "lower_arms": ["lower_arm_L", "lower_arm_R"],
    "hands": ["hand_L", "hand_R"],
    "upper_legs": ["upper_leg_L", "upper_leg_R"],
    "lower_legs": ["lower_leg_L", "lower_leg_R"],
    "feet": ["foot_L", "foot_R"],
}
HIDE_HAIR = "hair"

# Regions that an item of a slot must hide, so the body never shows through a garment.
REQUIRED_MASKS: Dict[str, List[str]] = {"top": ["torso"], "bottom": ["hips"], "shoes": ["feet"]}

# Bones where an accessory can attach. A left bone mirrors to the right bone at attachment point 2.
ATTACH_BONES = ["head", "neck", "chest", "lower_arm_L", "hand_L"]


def mirror_name(name: str) -> str:
    if name.endswith("_L"):
        return name[:-2] + "_R"
    if name.endswith("_R"):
        return name[:-2] + "_L"
    return name


@dataclass(frozen=True)
class Dims:
    """Sizes of a body preset. The joints never move (one rig); only widths change."""

    torso_x: float = 1.0
    torso_y: float = 1.0
    hips_x: float = 1.0
    limb: float = 1.0
    arm_out: float = 0.0
    # Torso rings: (z, half width, half depth).
    torso: Tuple[Tuple[float, float, float], ...] = (
        (0.86, 0.122, 0.082),
        (0.96, 0.132, 0.088),
        (1.05, 0.158, 0.094),
        (1.10, 0.138, 0.084),
        (1.135, 0.072, 0.056),
    )
    pelvis: Tuple[Tuple[float, float, float], ...] = (
        (0.86, 0.122, 0.082),
        (0.78, 0.134, 0.088),
        (0.71, 0.104, 0.072),
    )

    def torso_ring(self, i: int) -> Tuple[float, float, float]:
        z, x, y = self.torso[i]
        return z, x * self.torso_x, y * self.torso_y

    def pelvis_ring(self, i: int) -> Tuple[float, float, float]:
        z, x, y = self.pelvis[i]
        return z, x * self.hips_x, y * self.hips_x

    def torso_at(self, z: float) -> Tuple[float, float]:
        """Half width and depth of the body (torso or pelvis) at height z (linear between rings)."""
        rings = sorted([self.pelvis_ring(i) for i in range(len(self.pelvis))] +
                       [self.torso_ring(i) for i in range(1, len(self.torso))])
        if z <= rings[0][0]:
            return rings[0][1], rings[0][2]
        for (z0, x0, y0), (z1, x1, y1) in zip(rings, rings[1:]):
            if z0 <= z <= z1:
                t = (z - z0) / (z1 - z0)
                return x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
        return rings[-1][1], rings[-1][2]

    def shoulder(self) -> Vec:
        return (SHOULDER[0] + self.arm_out, SHOULDER[1], SHOULDER[2])


BODY_A = Dims()
# Body B: broader shoulders and chest, thicker limbs. Not a gender: both bodies wear every item.
BODY_B = replace(BODY_A, torso_x=1.3, torso_y=1.16, hips_x=1.14, limb=1.22, arm_out=0.032)

BODY_PRESETS: Dict[str, Dims] = {"body_a": BODY_A, "body_b": BODY_B}
MORPH_BODY_B = "body_b"
