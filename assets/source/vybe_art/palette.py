"""Colors (docs/art/README.md). Hex values are sRGB; Blender and glTF store them as linear values.

Skin tones and hair colors keep the ids of assets/manifests/avatar.v1.json and the colors of the 2D portrait,
so the 3D avatar and the 2D fallback look the same.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, Optional


@dataclass(frozen=True)
class Material:
    name: str
    color: str
    emissive: Optional[str] = None
    double_sided: bool = False


SKIN_TONES: Dict[str, str] = {
    "skin_01": "#f6d7c3",
    "skin_02": "#eac0a0",
    "skin_03": "#c99a72",
    "skin_04": "#a87650",
    "skin_05": "#7c5236",
    "skin_06": "#53351f",
}

HAIR_COLORS: Dict[str, str] = {
    "black": "#1c1c22",
    "brown": "#6b4226",
    "blond": "#e0bd6e",
    "red": "#a8432a",
    "blue": "#3f6ee8",
}

DEFAULT_SKIN = SKIN_TONES["skin_03"]
DEFAULT_HAIR = HAIR_COLORS["brown"]

# Materials whose color the viewer sets from the avatar presets (spec §9: no realistic skin, flat colors).
RUNTIME_MATERIALS = {"skin": "skinToneId", "hair": "hairColorId"}

BASE: Dict[str, Material] = {
    "skin": Material("skin", DEFAULT_SKIN),
    "hair": Material("hair", DEFAULT_HAIR, double_sided=True),
    "ink": Material("ink", "#1c1c22"),
    "shine": Material("shine", "#ffffff"),
}


def materials(**colors: str) -> Dict[str, Material]:
    """Garment materials (double-sided: openings show the inside) plus the base set."""
    out = dict(BASE)
    for name, value in colors.items():
        if "|" in value:
            color, glow = value.split("|")
            out[name] = Material(name, color, emissive=glow, double_sided=True)
        else:
            out[name] = Material(name, value, double_sided=True)
    return out
