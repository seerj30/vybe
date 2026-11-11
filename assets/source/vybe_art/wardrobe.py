"""Parametric garments and accessories. Every builder takes the body sizes (Dims) and returns meshes.

The same builder runs for body A and body B; the body B result becomes the "body_b" shape key, so one file
fits both bodies (spec §9: modular assets, no unique full-body meshes).
"""

from __future__ import annotations

import math
from typing import Callable, Dict, List, Optional, Sequence, Tuple

from . import geom as g
from .body import FLAT, FOOT_RINGS, HEAD_CENTER, HEAD_RADII, foot, hair_cap, hairline, hip_point, on_head
from .body import _surface_y as head_surface_y
from .geom import Mesh, Vec, blend
from .rig import Dims, mirror_name

# ---------------------------------------------------------------- helpers

_TORSO_W: List[Tuple[float, g.WeightSpec]] = [
    (0.71, "hips"),
    (0.78, "hips"),
    (0.86, {"hips": 0.5, "spine": 0.5}),
    (0.96, "spine"),
    (1.05, {"chest": 0.7, "spine": 0.3}),
    (1.10, "chest"),
]


def torso_weight(z: float) -> g.Weights:
    """Weights of the body at height z (the same as the body rings), so garments bend with the body."""
    if z <= _TORSO_W[0][0]:
        return g.as_weights(_TORSO_W[0][1])
    for (z0, w0), (z1, w1) in zip(_TORSO_W, _TORSO_W[1:]):
        if z0 <= z <= z1:
            return blend(w0, w1, (z - z0) / (z1 - z0))
    return g.as_weights(_TORSO_W[-1][1])


def both_sides(left: Mesh, name: str) -> Mesh:
    out = Mesh(name)
    out.merge(left)
    out.merge(left.mirrored(name, mirror_name))
    return out


def _band_mat(zs: Sequence[float], fn: Callable[[float], str]) -> Callable[[int, int], str]:
    return lambda band, _side: fn((zs[band] + zs[band + 1]) / 2)


# ---------------------------------------------------------------- tops


def top(
    d: Dims,
    name: str,
    sleeves: str = "short",
    fit: float = 0.016,
    scale: float = 1.05,
    hem: float = 0.80,
    tank: bool = False,
    body: Callable[[float], str] = lambda _z: "primary",
    sleeve: str = "primary",
    trim: Optional[str] = "trim",
    cuff: Optional[str] = None,
    hood: bool = False,
    pocket: bool = False,
    collar: bool = False,
    zipper: Optional[str] = None,
    puffy: float = 0.0,
    stars: Optional[str] = None,
    zs_override: Optional[Sequence[float]] = None,
) -> Mesh:
    m = Mesh(name)
    zs = [hem, 0.86, 0.96, 1.05, 1.10, 1.15] if hem < 0.86 else [hem, 0.96, 1.05, 1.10, 1.15]
    if zs_override:
        zs = list(zs_override)
    if puffy:
        zs = [hem + (1.10 - hem) * i / 7 for i in range(8)] + [1.15]
    rings = []
    for i, z in enumerate(zs):
        if z >= 1.15:
            rx, ry = 0.088 * d.torso_x + fit * 0.6, 0.068 * d.torso_y + fit * 0.6
        else:
            x, y = d.torso_at(z)
            rx, ry = x * scale + fit, y * scale + fit
            if tank and z >= 1.10:
                rx = 0.112 * d.torso_x + fit
            if puffy and 0 < i < len(zs) - 2 and i % 2 == 1:
                rx, ry = rx + puffy, ry + puffy
        rings.append(g.ring((0, 0, z), (0, 0, 1), rx, ry, 8, FLAT))
    g.loft(m, rings, [torso_weight(min(z, 1.10)) for z in zs], _band_mat(zs, body))
    if trim:
        neck = [g.add(p, (0, 0, 0.004)) for p in rings[-1]]
        g.path_tube(m, neck, 0.011, "chest", trim, sides=4, closed=True)
        if hem < 0.86:
            hx, hy = _ring_r(rings[0])
            hem_ring = g.ring((0, 0, hem + 0.008), (0, 0, 1), hx + 0.004, hy + 0.004, 8, FLAT)
            g.path_tube(m, hem_ring, 0.009, torso_weight(hem), trim, sides=4, closed=True)
    if sleeves != "none":
        m.merge(both_sides(_sleeve(d, sleeves, fit, sleeve, cuff or trim, puffy), name))
    if hood:
        _hood(m, d, body(1.0), trim)
    if pocket:
        x, y = d.torso_at(0.89)
        g.box(m, (0, -(y * scale + fit) - 0.004, 0.89), (0.17 * d.torso_x, 0.012, 0.075), torso_weight(0.89),
              body(0.95) if trim is None else (trim if hood else body(0.95)), top_scale=(0.78, 1.0))
    if collar:
        c0 = g.ring((0, 0, 1.125), (0, 0, 1), 0.098 * d.torso_x + fit, 0.078 * d.torso_y + fit, 8, FLAT)
        c1 = g.ring((0, 0, 1.2), (0, 0, 1), 0.084 * d.torso_x + fit, 0.07 * d.torso_y + fit, 8, FLAT)
        g.loft(m, [c0, c1], ["chest", blend("chest", "neck", 0.5)], trim or body(1.1))
    if zipper:
        zs2 = [hem + 0.004, 1.13]
        pts = []
        for z in zs2:
            _x, y = d.torso_at(min(z, 1.10))
            pts.append((0.0, -(y * scale + fit) - 0.003, z))
        g.path_tube(m, pts, 0.006, lambda i: torso_weight(min(zs2[i], 1.10)), zipper, sides=4)
    if stars:
        for (sx, z) in [(-0.07, 1.0), (0.06, 0.93), (0.02, 1.05), (-0.03, 0.86), (0.09, 1.04)]:
            x, y = d.torso_at(z)
            g.icosahedron(m, (sx * d.torso_x, -(y * scale + fit) - 0.002, z), (0.014, 0.008, 0.014), torso_weight(z), stars)
    return m


def _ring_r(r: g.Ring) -> Tuple[float, float]:
    xs = [abs(p[0]) for p in r]
    ys = [abs(p[1]) for p in r]
    return max(xs), max(ys)


def _sleeve(d: Dims, kind: str, fit: float, mat: str, cuff: Optional[str], puffy: float) -> Mesh:
    """Left sleeve: covers the shoulder, ends past the elbow (short) or at the wrist (long)."""
    m = Mesh("sleeve")
    k = d.limb
    s, elbow, wrist = d.shoulder(), d.elbow(), d.wrist()
    ud = g.norm(g.sub(elbow, s))
    ad = d.arm_dir()
    pts: List[Tuple[Vec, Vec, float, g.WeightSpec]] = [
        (g.sub(s, g.mul(ud, 0.045)), ud, 0.058 * k + fit, "upper_arm_L"),
        (s, ud, 0.066 * k + fit, "upper_arm_L"),
        (g.lerp(s, elbow, 0.55), ud, 0.054 * k + fit + puffy, "upper_arm_L"),
    ]
    if kind == "short":
        pts.append((g.add(elbow, g.mul(ad, 0.012)), g.norm(g.add(ud, ad)), 0.05 * k + fit + 0.008, blend("upper_arm_L", "lower_arm_L", 0.5)))
    else:
        pts += [
            (elbow, g.norm(g.add(ud, ad)), 0.048 * k + fit, blend("upper_arm_L", "lower_arm_L", 0.5)),
            (g.lerp(elbow, wrist, 0.5), ad, 0.044 * k + fit + puffy, "lower_arm_L"),
            (g.add(wrist, g.mul(ad, 0.006)), ad, 0.04 * k + fit, blend("lower_arm_L", "hand_L", 0.5)),
        ]
    rings = [g.ring(c, dd, r, r, 8, FLAT) for (c, dd, r, _w) in pts]
    g.loft(m, rings, [w for (_c, _d, _r, w) in pts], mat, cap_start=True, cap_mat=mat)
    if cuff:
        c, dd, r, w = pts[-1]
        g.path_tube(m, g.ring(g.sub(c, g.mul(dd, 0.008)), dd, r + 0.003, r + 0.003, 8, FLAT), 0.008, w, cuff, sides=4,
                    closed=True)
    return m


def _hood(m: Mesh, d: Dims, mat: str, string: Optional[str]) -> None:
    g.ellipsoid(m, (0, 0.105 * d.torso_y, 1.17), (0.12 * d.torso_x, 0.065, 0.075), blend("chest", "neck", 0.3), mat,
                lon=8, lat=5)
    if string:
        for side in (1, -1):
            _x, y = d.torso_at(1.05)
            g.path_tube(m, [(side * 0.04, -y * 1.05 - 0.03, 1.12), (side * 0.045, -y * 1.05 - 0.03, 1.0)], 0.005,
                        "chest", string, sides=4)


# ---------------------------------------------------------------- bottoms


def bottom(
    d: Dims,
    name: str,
    length: str = "shorts",
    fit: float = 0.011,
    scale: float = 1.02,
    leg_fit: float = 0.016,
    flare: float = 0.01,
    main: str = "primary",
    band: Optional[str] = "trim",
    stripe: Optional[str] = None,
    cuff: Optional[str] = None,
    pockets: Optional[str] = None,
) -> Mesh:
    m = Mesh(name)
    zs = [0.70, 0.78, 0.86, 0.885]
    rings = []
    for z in zs:
        x, y = d.torso_at(max(z, 0.71))
        rings.append(g.ring((0, 0, z), (0, 0, 1), x * scale + fit, y * scale + fit, 8, FLAT))
    mats = (lambda b, _s: band if (band and b == len(zs) - 2) else main)
    g.loft(m, rings, [torso_weight(z) for z in zs], mats, cap_start=True, cap_mat=main)
    m.merge(both_sides(_leg(d, length, leg_fit, flare, main, stripe, cuff, pockets), name))
    return m


def _leg(d: Dims, length: str, fit: float, flare: float, main: str, stripe: Optional[str], cuff: Optional[str],
         pockets: Optional[str]) -> Mesh:
    m = Mesh("leg")
    k = d.limb
    hip, knee, ankle = hip_point(d), d.knee(), d.ankle()
    pts: List[Tuple[Vec, float, g.WeightSpec]] = [
        (g.add(hip, (0, 0, 0.03)), 0.08 * k + fit, "upper_leg_L"),
        (g.lerp(hip, knee, 0.5), 0.07 * k + fit, "upper_leg_L"),
    ]
    if length == "shorts":
        pts.append((g.add(knee, (0, 0, -0.025)), 0.058 * k + fit + flare, blend("upper_leg_L", "lower_leg_L", 0.5)))
    else:
        pts += [
            (knee, 0.058 * k + fit, blend("upper_leg_L", "lower_leg_L", 0.5)),
            (g.lerp(knee, ankle, 0.35), 0.062 * k + fit, "lower_leg_L"),
            (g.add(ankle, (0, 0, 0.005)), 0.046 * k + fit + flare, blend("lower_leg_L", "foot_L", 0.5)),
        ]
    rings = [g.ring(c, (0, 0, -1), r, r, 8, FLAT) for (c, r, _w) in pts]

    def mat(band: int, side: int) -> str:
        if cuff and length != "shorts" and band == len(pts) - 2:
            return cuff
        if stripe and side == 7:
            return stripe
        return main

    g.loft(m, rings, [w for (_c, _r, w) in pts], mat)
    if pockets:
        z = 0.57
        r = 0.07 * k + fit
        g.box(m, (hip[0] + r * 0.92, 0.0, z), (0.022, 0.08, 0.1), "upper_leg_L", pockets, top_scale=(1.0, 0.9))
    return m


# ---------------------------------------------------------------- shoes


def shoe(
    d: Dims,
    name: str,
    kind: str = "low",
    main: str = "primary",
    sole: str = "sole",
    toe: Optional[str] = None,
    laces: Optional[str] = "trim",
    collar: Optional[str] = None,
    stripe: Optional[str] = None,
) -> Mesh:
    left = Mesh("shoe")
    k = d.limb
    ax = d.ankle()[0]
    grow = 0.018 if kind == "boot" else 0.012
    sole_h = 0.04 if kind == "boot" else 0.028
    g.box(left, (ax, -0.04, sole_h / 2), (0.1 * k + 2 * grow, 0.215, sole_h), "foot_L", sole, top_scale=(1.0, 0.98))
    if kind == "slide":
        foot(left, d, 0.0, "skin", "foot_L", lift=sole_h - 0.004)
        g.box(left, (ax, -0.045, sole_h + 0.03), (0.1 * k + 0.03, 0.06, 0.024), "foot_L", main, top_scale=(0.8, 0.9))
        return both_sides(left, name)

    def mat(band: int, side: int) -> str:
        if toe and band == len(FOOT_RINGS) - 2:
            return toe
        if stripe and side in (6, 7) and band in (1, 2):
            return stripe
        return main

    foot(left, d, grow, mat, "foot_L", lift=0.006)
    if kind in ("high", "boot"):
        top_z = 0.23 if kind == "boot" else 0.2
        r0, r1 = 0.05 * k + grow, 0.047 * k + grow
        c0, c1 = (ax, 0.012, 0.05), (ax, 0.0, top_z)
        g.loft(left, [g.ring(c0, (0, 0, 1), r0, r0 * 1.15, 8, FLAT), g.ring(c1, (0, 0, 1), r1, r1, 8, FLAT)],
               [blend("lower_leg_L", "foot_L", 0.5), "lower_leg_L"], main)
        if collar:
            g.path_tube(left, g.ring(g.add(c1, (0, 0, -0.006)), (0, 0, 1), r1 + 0.004, r1 + 0.004, 8, FLAT), 0.009,
                        "lower_leg_L", collar, sides=4, closed=True)
    if laces:
        for i in range(3 if kind == "low" else 4):
            y = -0.035 - 0.025 * i if kind == "low" else 0.0 - 0.028 * i
            z = 0.095 - 0.011 * i if kind == "low" else 0.12 - 0.016 * i
            g.box(left, (ax, y - 0.012 - (0.012 if kind != "low" else 0), z), (0.05 * k, 0.01, 0.008), "foot_L", laces)
    return both_sides(left, name)


# ---------------------------------------------------------------- hair slot


def hair_item(kind: str, name: str) -> Mesh:
    m = Mesh(name)
    if kind == "high_top":
        hair_cap(m, 0.02, hairline(52, 86, 100))
        g.tube(m, (0, 0.01, 1.47), (0, 0.01, 1.68), [(0, 0.17, 0.16), (1, 0.165, 0.155)], ["head", "head"], "hair",
               sides=10)
    elif kind == "mohawk":
        hair_cap(m, 0.006, hairline(50, 82, 98))
        for (th, ph) in [(-90, 46), (-90, 26), (-90, 6), (90, 14), (90, 34), (90, 54)]:
            base = on_head(th, ph, 0.0)
            tip = on_head(th, ph, 0.11)
            g.cone(m, base, tip, 0.05, "head", "hair", sides=4, phase=math.pi / 4)
    elif kind == "bun":
        hair_cap(m, 0.016, hairline(48, 88, 100))
        g.ellipsoid(m, on_head(90, 28, 0.065), (0.075, 0.07, 0.07), "head", "hair", lon=8, lat=5)
        g.path_tube(m, g.ring(on_head(90, 32, 0.02), g.sub(on_head(90, 28, 0.1), HEAD_CENTER), 0.05, 0.05, 8), 0.01,
                    "head", "accent", sides=4, closed=True)
    elif kind == "spikes":
        hair_cap(m, 0.018, hairline(48, 86, 100))
        for (th, ph) in [(-90, 40), (-50, 50), (-130, 50), (-90, 12), (0, 40), (180, 40), (90, 20), (40, 60), (140, 60)]:
            g.cone(m, on_head(th, ph, 0.0), on_head(th, ph, 0.12), 0.045, "head", "hair", sides=4)
    elif kind == "braids":
        hair_cap(m, 0.016, hairline(50, 90, 104))
        for side in (1, -1):
            x0, y0 = side * 0.12, 0.13
            for i in range(7):
                z = 1.24 - 0.045 * i
                g.icosahedron(m, (x0 * (1 - 0.03 * i), y0 + 0.01 * i, z), (0.032, 0.032, 0.03),
                              "head" if z > 1.15 else blend("head", "neck", 0.5), "hair")
            g.tube(m, (x0 * 0.82, y0 + 0.07, 0.95), (x0 * 0.82, y0 + 0.07, 0.92), [(0, 0.026, 0.026), (1, 0.026, 0.026)],
                   [blend("head", "neck", 0.5)] * 2, "accent", sides=6)
    elif kind == "flame":
        hair_cap(m, 0.02, hairline(50, 86, 100), mat="flame_a")
        for (th, ph, h) in [(-90, 34, 0.15), (-50, 44, 0.12), (-130, 44, 0.12), (-90, 8, 0.2), (0, 40, 0.12),
                            (180, 40, 0.12), (90, 22, 0.16), (45, 55, 0.1), (135, 55, 0.1)]:
            base = on_head(th, ph, 0.0)
            tip = g.add(on_head(th, ph, h * 0.6), (0, 0.03, h * 0.6))
            g.cone(m, base, tip, 0.055, "head", "flame_b", sides=5)
    else:
        raise ValueError(kind)
    return m


# ---------------------------------------------------------------- accessories


def sweatband(d: Dims, name: str) -> Mesh:
    m = Mesh(name)
    ad = d.arm_dir()
    k = d.limb
    wrist = d.wrist()
    a, b = g.sub(wrist, g.mul(ad, 0.075)), g.sub(wrist, g.mul(ad, 0.015))
    r0, r1 = 0.039 * k + 0.012, 0.036 * k + 0.012
    rings = [g.ring(g.lerp(a, b, t), ad, r0 + (r1 - r0) * t, r0 + (r1 - r0) * t, 8, FLAT) for t in (0, 0.38, 0.62, 1)]
    g.loft(m, rings, ["lower_arm_L"] * 4, lambda band, _s: "accent" if band == 1 else "primary", True, True)
    return m


def chain(d: Dims, name: str) -> Mesh:
    m = Mesh(name)
    pts = []
    n = 16
    for i in range(n):
        a = 2 * math.pi * i / n
        x = 0.1 * d.torso_x * math.cos(a)
        y = 0.085 * d.torso_y * math.sin(a) - 0.005
        z = 1.12 + 0.05 * math.sin(a)  # higher at the back (+Y), lower at the front
        pts.append((x, y, z))
    g.path_tube(m, pts, 0.008, "chest", "metal", sides=4, closed=True)
    _x, y = d.torso_at(1.04)
    g.oriented_box(m, (0, -y * 1.0 - 0.02, 1.035), ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (0.022, 0.006, 0.026), "chest",
                   "metal")
    g.icosahedron(m, (0, -y - 0.028, 1.035), (0.012, 0.006, 0.012), "chest", "gem")
    return m


def snapback(name: str) -> Mesh:
    m = Mesh(name)
    g.cap_shell(m, g.add(HEAD_CENTER, (0, 0, 0.012)), (HEAD_RADII[0] + 0.03, HEAD_RADII[1] + 0.03, HEAD_RADII[2] + 0.03),
                lambda th: math.radians(80 if math.sin(th) < 0 else 92), "head", "primary", lon=12, rows=4)
    brim_c = on_head(-90, 74, 0.04)
    g.box(m, (0, brim_c[1] - 0.08, brim_c[2] - 0.005), (0.25, 0.16, 0.014), "head", "secondary", top_scale=(0.96, 0.96))
    g.ellipsoid(m, g.add(HEAD_CENTER, (0, 0, HEAD_RADII[2] + 0.045)), (0.02, 0.02, 0.012), "head", "secondary", lon=6, lat=3)
    return m


def beanie(name: str) -> Mesh:
    m = Mesh(name)
    g.cap_shell(m, g.add(HEAD_CENTER, (0, 0, 0.02)), (HEAD_RADII[0] + 0.03, HEAD_RADII[1] + 0.03, HEAD_RADII[2] + 0.05),
                lambda th: math.radians(82 if math.sin(th) < 0 else 94), "head", "primary", lon=12, rows=4)
    c = g.add(HEAD_CENTER, (0, 0, 0.035))
    r0 = g.ring(c, (0, 0, 1), HEAD_RADII[0] + 0.04, HEAD_RADII[1] + 0.04, 12)
    r1 = g.ring(g.add(c, (0, 0, 0.065)), (0, 0, 1), HEAD_RADII[0] + 0.035, HEAD_RADII[1] + 0.035, 12)
    g.loft(m, [r0, r1], ["head", "head"], "secondary")
    g.icosahedron(m, g.add(HEAD_CENTER, (0, 0, HEAD_RADII[2] + 0.085)), (0.05, 0.05, 0.05), "head", "secondary")
    return m


def shades(name: str) -> Mesh:
    m = Mesh(name)
    z = 1.38
    for s in (1, -1):
        x = s * 0.07
        y = head_surface_y(x, z) - 0.02
        g.box(m, (x, y, z), (0.085, 0.012, 0.052), "head", "lens", top_scale=(1.08, 1.0))
        g.path_tube(m, [(s * 0.115, y + 0.004, z + 0.012), (s * 0.198, 0.0, z + 0.018), (s * 0.2, 0.05, z)], 0.006, "head",
                    "primary", sides=4)
    yb = head_surface_y(0, z) - 0.02
    g.box(m, (0, yb, z + 0.012), (0.06, 0.012, 0.012), "head", "primary")
    return m


def headphones(name: str) -> Mesh:
    m = Mesh(name)
    pts = [on_head(0, 80 - a, 0.06) for a in range(0, 81, 10)] + [on_head(180, a, 0.06) for a in range(10, 81, 10)]
    g.path_tube(m, pts, 0.014, "head", "primary", sides=6)
    for s in (1, -1):
        g.tube(m, (s * (HEAD_RADII[0] + 0.005), 0.01, 1.35), (s * (HEAD_RADII[0] + 0.055), 0.01, 1.35),
               [(0, 0.06, 0.07), (1, 0.055, 0.065)], ["head", "head"], "secondary", sides=10)
    return m


def backpack(d: Dims, name: str) -> Mesh:
    m = Mesh(name)
    _x, y = d.torso_at(1.0)
    back = y * d.torso_y + 0.02
    g.box(m, (0, back + 0.055, 0.99), (0.24 * d.torso_x, 0.1, 0.27), "chest", "primary", top_scale=(0.9, 0.85))
    g.box(m, (0, back + 0.11, 0.95), (0.16 * d.torso_x, 0.03, 0.11), "chest", "secondary")
    g.box(m, (0, back + 0.055, 1.135), (0.2 * d.torso_x, 0.07, 0.035), "chest", "secondary")
    for s in (1, -1):
        x = s * 0.075 * d.torso_x
        _x1, yf = d.torso_at(0.9)
        _x2, yc = d.torso_at(1.05)
        pts = [(x, back + 0.01, 1.1), (x * 1.15, 0.0, 1.145), (x * 1.15, -(yc * 1.02 + 0.02), 1.06),
               (x * 1.25, -(yf + 0.025), 0.9)]
        g.path_tube(m, pts, 0.011, "chest", "secondary", sides=4)
    return m


def crown(name: str) -> Mesh:
    m = Mesh(name)
    c = (0, 0, HEAD_CENTER[2] + HEAD_RADII[2] - 0.035)
    r = 0.13
    rings = [g.ring(g.add(c, (0, 0, dz)), (0, 0, 1), r + dz * 0.2, r * 0.95 + dz * 0.2, 10) for dz in (0.0, 0.045)]
    g.loft(m, rings, ["head", "head"], "metal")
    for i in range(5):
        a = 2 * math.pi * i / 5 - math.pi / 2
        base = (c[0] + (r + 0.009) * math.cos(a), c[1] + (r * 0.95 + 0.009) * math.sin(a), c[2] + 0.04)
        g.cone(m, base, g.add(base, (0, 0, 0.075)), 0.03, "head", "metal", sides=4, phase=math.pi / 4)
        g.icosahedron(m, (c[0] + (r + 0.012) * math.cos(a), c[1] + (r * 0.95 + 0.012) * math.sin(a), c[2] + 0.022),
                      (0.014, 0.014, 0.014), "head", "gem")
    return m


def bucket_hat(name: str) -> Mesh:
    """Soft dome and a brim that slopes down all around (hides the hair)."""
    m = Mesh(name)
    g.cap_shell(m, g.add(HEAD_CENTER, (0, 0, 0.015)), (HEAD_RADII[0] + 0.03, HEAD_RADII[1] + 0.03, HEAD_RADII[2] + 0.04),
                lambda th: math.radians(78 if math.sin(th) < 0 else 90), "head", "primary", lon=12, rows=4)
    c = g.add(HEAD_CENTER, (0, 0, 0.05))
    band = [g.ring(g.add(c, (0, 0, dz)), (0, 0, 1), HEAD_RADII[0] + 0.035, HEAD_RADII[1] + 0.035, 12) for dz in (0.0, 0.035)]
    g.loft(m, band, ["head", "head"], "secondary")
    inner = g.ring(g.add(c, (0, 0, 0.002)), (0, 0, 1), HEAD_RADII[0] + 0.034, HEAD_RADII[1] + 0.034, 12)
    outer = g.ring(g.add(c, (0, 0, -0.05)), (0, 0, 1), HEAD_RADII[0] + 0.11, HEAD_RADII[1] + 0.11, 12)
    g.loft(m, [outer, inner], ["head", "head"], "primary")
    return m


def headband(name: str) -> Mesh:
    """A band around the forehead with a small badge at the front."""
    m = Mesh(name)
    rings = []
    for dz in (0.07, 0.115):
        s = math.sqrt(1 - (dz / HEAD_RADII[2]) ** 2)  # the head gets narrower above its center
        rings.append(g.ring(g.add(HEAD_CENTER, (0, 0, dz)), (0, 0, 1), HEAD_RADII[0] * s + 0.014,
                            HEAD_RADII[1] * s + 0.014, 12))
    g.loft(m, rings, ["head", "head"], "primary")
    z = HEAD_CENTER[2] + 0.092
    y = head_surface_y(0, z) - 0.012
    g.box(m, (0, y, z), (0.05, 0.012, 0.03), "head", "accent")
    return m
