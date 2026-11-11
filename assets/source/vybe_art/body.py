"""The base avatar: body regions, face presets and preset hair styles (assets/manifests/avatar.v1.json ids)."""

from __future__ import annotations

import math
from typing import Callable, Dict, List

from . import geom as g
from .geom import Mesh, Vec, blend
from .rig import BODY_A, Dims

SKIN = "skin"
HAIR = "hair"
INK = "ink"
SHINE = "shine"

# Head: a 12-sided low-poly ellipsoid. Hair and hats are shells around the same ellipsoid.
HEAD_CENTER: Vec = (0.0, 0.0, 1.36)
HEAD_RADII: Vec = (0.2, 0.185, 0.2)
HEAD_LON = 12

FLAT = math.pi / 8  # ring phase with a flat face at the front for 8-sided rings


def hip_point(d: Dims) -> Vec:
    return d.hip()


# ---------------------------------------------------------------- body regions


def torso(d: Dims) -> Mesh:
    m = Mesh("region_torso")
    rings = []
    for i in range(len(d.torso)):
        z, x, y = d.torso_ring(i)
        rings.append(g.ring((0, 0, z), (0, 0, 1), x, y, 8, FLAT))
    w = [blend("hips", "spine", 0.5), "spine", blend("spine", "chest", 0.7), "chest", "chest"]
    g.loft(m, rings, w, SKIN, cap_end=True)
    return m


def pelvis(d: Dims) -> Mesh:
    m = Mesh("region_hips")
    rings = []
    for i in reversed(range(len(d.pelvis))):
        z, x, y = d.pelvis_ring(i)
        rings.append(g.ring((0, 0, z), (0, 0, 1), x, y, 8, FLAT))
    g.loft(m, rings, ["hips", "hips", blend("hips", "spine", 0.5)], SKIN, cap_start=True)
    return m


def neck(d: Dims) -> Mesh:
    m = Mesh("region_neck")
    r = 0.045 * d.limb
    g.tube(m, (0, 0, 1.09), (0, 0, 1.24), [(0, r, r), (1, r, r)], ["neck", "neck"], SKIN, sides=6)
    return m


def head_mesh() -> Mesh:
    m = Mesh("region_head")
    g.ellipsoid(m, HEAD_CENTER, HEAD_RADII, "head", SKIN, lon=HEAD_LON, lat=8)
    return m


def head(d: Dims) -> Mesh:
    m = head_mesh()
    for side in (1, -1):
        g.ellipsoid(m, (side * 0.196, 0.008, 1.345), (0.022, 0.034, 0.044), "head", SKIN, lon=6, lat=4)
    nose_y = g.ray_y(head_mesh(), 0.0, 1.325)
    g.ellipsoid(m, (0.0, nose_y + 0.006, 1.325), (0.02, 0.018, 0.019), "head", SKIN, lon=6, lat=4)
    return m


def _arm_parts(d: Dims, side: int) -> Dict[str, Mesh]:
    """Left arm (side 1) parts; the right arm is the mirror."""
    s, elbow, wrist = d.shoulder(), d.elbow(), d.wrist()
    ld = g.norm(g.sub(elbow, s))
    up, lo, hd = Mesh("u"), Mesh("l"), Mesh("h")
    k = d.limb
    g.ellipsoid(up, g.add(s, (0.0, 0, 0.004)), (0.056 * k, 0.054 * k, 0.054 * k), "upper_arm_L", SKIN, lon=8, lat=4)
    g.tube(up, g.sub(s, g.mul(ld, 0.02)), elbow, [(0, 0.051 * k, 0.051 * k), (1, 0.044 * k, 0.044 * k)],
           ["upper_arm_L", blend("upper_arm_L", "lower_arm_L", 0.5)], SKIN, sides=8)
    g.tube(lo, elbow, wrist, [(0, 0.044 * k, 0.044 * k), (1, 0.035 * k, 0.035 * k)],
           [blend("upper_arm_L", "lower_arm_L", 0.5), blend("lower_arm_L", "hand_L", 0.5)], SKIN, sides=8)
    c = g.add(wrist, g.mul(d.arm_dir(), 0.045))
    g.ellipsoid(hd, c, (0.026 * k, 0.038 * k, 0.05 * k), "hand_L", SKIN, lon=8, lat=5)
    g.ellipsoid(hd, g.add(c, (-0.004, -0.032 * k, 0.012)), (0.014 * k, 0.016 * k, 0.024 * k), "hand_L", SKIN, lon=6, lat=4)
    return {"upper_arms": up, "lower_arms": lo, "hands": hd}


def _leg_parts(d: Dims) -> Dict[str, Mesh]:
    k = d.limb
    up, lo, ft = Mesh("u"), Mesh("l"), Mesh("f")
    knee, ankle = d.knee(), d.ankle()
    g.tube(up, hip_point(d), knee, [(0, 0.074 * k, 0.074 * k), (1, 0.056 * k, 0.056 * k)],
           ["upper_leg_L", blend("upper_leg_L", "lower_leg_L", 0.5)], SKIN, sides=8, phase=FLAT)
    g.tube(lo, knee, ankle, [(0, 0.056 * k, 0.056 * k), (0.3, 0.06 * k, 0.06 * k), (1, 0.041 * k, 0.041 * k)],
           [blend("upper_leg_L", "lower_leg_L", 0.5), "lower_leg_L", blend("lower_leg_L", "foot_L", 0.5)], SKIN,
           sides=8, phase=FLAT)
    foot(ft, d, 0.0, SKIN, "foot_L")
    return {"upper_legs": up, "lower_legs": lo, "feet": ft}


# Foot rings: (y, half width, half height, center z). The avatar faces -Y, so the toe is at negative y.
FOOT_RINGS = ((0.045, 0.040, 0.040, 0.042), (0.0, 0.046, 0.046, 0.046), (-0.08, 0.045, 0.036, 0.036),
              (-0.125, 0.034, 0.024, 0.027))


def foot(m: Mesh, d: Dims, grow: float, mat: g.MatFn, bone: str, lift: float = 0.0) -> None:
    rings = []
    for (y, hw, hh, cz) in FOOT_RINGS:
        rings.append(g.ring((d.ankle()[0], y, cz + lift), (0, -1, 0), hw * d.limb + grow, hh + grow, 8, FLAT))
    g.loft(m, rings, [bone] * len(rings), mat, cap_start=True, cap_end=True)


def _sides(parts_l: Mesh, name: str) -> Mesh:
    out = Mesh(name)
    out.merge(parts_l)
    from .rig import mirror_name
    out.merge(parts_l.mirrored(name, mirror_name))
    return out


def region_meshes(d: Dims) -> List[Mesh]:
    arms = _arm_parts(d, 1)
    legs = _leg_parts(d)
    return [
        head(d),
        neck(d),
        torso(d),
        pelvis(d),
        _sides(arms["upper_arms"], "region_upper_arms"),
        _sides(arms["lower_arms"], "region_lower_arms"),
        _sides(arms["hands"], "region_hands"),
        _sides(legs["upper_legs"], "region_upper_legs"),
        _sides(legs["lower_legs"], "region_lower_legs"),
        _sides(legs["feet"], "region_feet"),
    ]


# ---------------------------------------------------------------- faces

_HEAD = head_mesh()


def _surface_y(x: float, z: float) -> float:
    return g.ray_y(_HEAD, x, z)


def _disc(m: Mesh, x: float, z: float, rx: float, rz: float, mat: str, out: float = 0.004) -> None:
    y = _surface_y(x, z)
    g.tube(m, (x, y + 0.006, z), (x, y - out, z), [(0, rx, rz), (1, rx, rz)], ["head", "head"], mat, sides=8)


def _line(m: Mesh, pts: List[Vec], r: float = 0.0055, closed: bool = False) -> None:
    placed = [(x, _surface_y(x, z) - 0.002, z) for (x, _y, z) in pts]
    g.path_tube(m, placed, r, "head", INK, sides=4, closed=closed)


def _eyes(m: Mesh, rx: float, rz: float, z: float = 1.375, shine: bool = True) -> None:
    for s in (1, -1):
        _disc(m, s * 0.066, z, rx, rz, INK)
        if shine:
            _disc(m, s * 0.066 - 0.006, z + rz * 0.35, rx * 0.32, rx * 0.32, SHINE, out=0.0065)


def face(preset: str) -> Mesh:
    m = Mesh(preset)
    deg = math.radians
    if preset == "face_01":  # bright eyes, smile
        _eyes(m, 0.02, 0.027)
        _line(m, g.arc((0, 0, 1.318), 0.038, 0.022, deg(200), deg(340), 6, 0))
    elif preset == "face_02":  # happy closed eyes, wide smile
        for s in (1, -1):
            _line(m, g.arc((s * 0.066, 0, 1.362), 0.022, 0.016, deg(15), deg(165), 4, 0))
        _line(m, g.arc((0, 0, 1.322), 0.05, 0.03, deg(195), deg(345), 7, 0), r=0.0065)
    elif preset == "face_03":  # calm: dot eyes, brows, small mouth
        _eyes(m, 0.013, 0.016, shine=False)
        for s in (1, -1):
            _line(m, [(s * 0.045, 0, 1.41), (s * 0.088, 0, 1.414)], r=0.0045)
        _line(m, g.arc((0, 0, 1.33), 0.026, 0.008, deg(205), deg(335), 4, 0))
    elif preset == "face_04":  # cheerful: tall eyes, round mouth
        _eyes(m, 0.018, 0.032)
        _line(m, g.arc((0, 0, 1.305), 0.015, 0.017, 0, deg(330), 11, 0), closed=True)
    else:
        raise ValueError(preset)
    return m


FACES = ["face_01", "face_02", "face_03", "face_04"]


# ---------------------------------------------------------------- hair (presets and the hair slot)


def hairline(front: float, side: float, back: float) -> Callable[[float], float]:
    """Polar cut angle (degrees from the top) per longitude: front is -Y, back is +Y."""
    f, s_, b = math.radians(front), math.radians(side), math.radians(back)

    def cut(th: float) -> float:
        s = math.sin(th)
        return s_ + (f - s_) * (-s) if s < 0 else s_ + (b - s_) * s

    return cut


def hair_cap(m: Mesh, grow: float, cut: Callable[[float], float], mat: g.MatFn = HAIR, rows: int = 4,
             lift: float = 0.0) -> None:
    r = (HEAD_RADII[0] + grow, HEAD_RADII[1] + grow, HEAD_RADII[2] + grow)
    g.cap_shell(m, g.add(HEAD_CENTER, (0, 0, lift)), r, cut, "head", mat, lon=HEAD_LON, rows=rows)


def on_head(theta_deg: float, phi_deg: float, grow: float) -> Vec:
    """A point on the head ellipsoid, grown outward (theta: 0 = +X, -90 = front; phi: 0 = top)."""
    th, ph = math.radians(theta_deg), math.radians(phi_deg)
    return (
        HEAD_CENTER[0] + (HEAD_RADII[0] + grow) * math.sin(ph) * math.cos(th),
        HEAD_CENTER[1] + (HEAD_RADII[1] + grow) * math.sin(ph) * math.sin(th),
        HEAD_CENTER[2] + (HEAD_RADII[2] + grow) * math.cos(ph),
    )


def curtain(m: Mesh, top_z: float, bottom_z: float, r_top: Vec, r_bottom: Vec, th0: float, th1: float, cols: int,
            rows: int, mat: str = HAIR) -> None:
    """Open shell around the head axis from angle th0 to th1 (degrees, through the back)."""
    out = []
    for r in range(rows + 1):
        t = r / rows
        z = top_z + (bottom_z - top_z) * t
        rx = r_top[0] + (r_bottom[0] - r_top[0]) * t
        ry = r_top[1] + (r_bottom[1] - r_top[1]) * t
        row = []
        for c in range(cols + 1):
            th = math.radians(th0 + (th1 - th0) * c / cols)
            row.append((rx * math.cos(th), ry * math.sin(th) + r_top[2], z))
        out.append(row)
    g.sheet(m, out, lambda p: "head" if p[2] > 1.2 else blend("head", "neck", 0.5), mat)


def preset_hair(preset: str) -> Mesh:
    m = Mesh(preset)
    if preset == "hair_buzz":
        hair_cap(m, 0.007, hairline(50, 82, 98))
    elif preset == "hair_short":
        hair_cap(m, 0.02, hairline(46, 86, 102))
        g.cone(m, on_head(-90, 30, 0.0), on_head(-90, 48, 0.07), 0.05, "head", HAIR, sides=5)
        g.cone(m, on_head(-60, 35, 0.0), on_head(-62, 52, 0.06), 0.045, "head", HAIR, sides=5)
        g.cone(m, on_head(-120, 35, 0.0), on_head(-118, 52, 0.06), 0.045, "head", HAIR, sides=5)
    elif preset == "hair_long":
        hair_cap(m, 0.022, hairline(52, 95, 105))
        curtain(m, 1.40, 1.07, (0.215, 0.2, 0.01), (0.2, 0.16, 0.03), -25, 205, 10, 4)
    elif preset == "hair_curly":
        hair_cap(m, 0.016, hairline(48, 86, 100))
        for (th, ph) in [(-90, 40), (-50, 45), (-130, 45), (0, 55), (180, 55), (-20, 75), (-160, 75), (90, 40),
                         (40, 60), (140, 60), (90, 80), (0, 20), (180, 20), (-90, 10)]:
            g.icosahedron(m, on_head(th, ph, 0.025), (0.05, 0.05, 0.05), "head", HAIR)
    else:
        raise ValueError(preset)
    return m


PRESET_HAIRS = ["hair_short", "hair_long", "hair_curly", "hair_buzz"]


def base_meshes(d: Dims = BODY_A) -> List[Mesh]:
    """Everything in the base avatar file. Faces and hairs do not change with the body preset."""
    return region_meshes(d) + [face(f) for f in FACES] + [preset_hair(h) for h in PRESET_HAIRS]
