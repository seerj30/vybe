"""My Room (docs/design/p1-my-room.md): the room shell, wall and floor finishes, furniture and wall decor.

Plain Python geometry like the avatar (no Blender import). Room items are static meshes: no rig, no skin.

Conventions (Blender scene space, Z up; glTF is Y up and the exporter turns -Y into +Z):
- The floor is 8 x 8 cells of 0.5 m, centered on the origin: x and y from -2 m to 2 m. The back wall is at
  y = +2 m (glTF z = -2), the left wall at x = -2 m.
- A furniture mesh has its origin at the center of its footprint on the floor and its front to -Y (glTF +Z).
- A wall-decor mesh has its origin at the bottom center of its back face (y = 0) and its front to -Y.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

from . import geom as g
from .geom import Mesh, Vec
from .palette import Material

ROOM_VERSION = 1
CELL = 0.5
FLOOR_CELLS = (8, 8)  # x (left to right), y (back to front)
WALL_CELLS = (8, 5)  # columns, rows
WALL_HEIGHT = WALL_CELLS[1] * CELL
WALL_THICKNESS = 0.1
HALF = FLOOR_CELLS[0] * CELL / 2
MAX_ITEMS = 40
SURFACES = ("floor", "wall_back", "wall_left")
ROTATIONS = (0, 90, 180, 270)
W = "root"  # static meshes: the weight is a placeholder; Blender gets no vertex groups for them


def mats(**colors: str) -> Dict[str, Material]:
    """Room materials: single-sided (closed meshes), "#color|#glow" makes an emissive material."""
    out: Dict[str, Material] = {}
    for name, value in colors.items():
        if "|" in value:
            color, glow = value.split("|")
            out[name] = Material(name, color, emissive=glow)
        else:
            out[name] = Material(name, value)
    return out


# ---------------------------------------------------------------- helpers


def bx(m: Mesh, x0: float, x1: float, y0: float, y1: float, z0: float, z1: float, mat: str) -> None:
    """Axis-aligned box from its bounds."""
    g.box(m, ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0), W, mat)


def plate(m: Mesh, x0: float, x1: float, y0: float, y1: float, z0: float, z1: float, mat: str, skip: str) -> None:
    """A box without one face (skip: "-y", "+y", "-x" or "-z"): tiles, bricks and stripes on a flat surface."""
    p = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    i = [m.vert(q, W) for q in p]
    faces = {
        "-z": (i[0], i[3], i[2], i[1]), "+z": (i[4], i[5], i[6], i[7]), "-y": (i[0], i[1], i[5], i[4]),
        "+x": (i[1], i[2], i[6], i[5]), "+y": (i[2], i[3], i[7], i[6]), "-x": (i[3], i[0], i[4], i[7]),
    }
    for key, f in faces.items():
        if key != skip:
            m.face(f, mat)


def cyl(m: Mesh, x: float, y: float, z0: float, z1: float, r0: float, mat: str, r1: Optional[float] = None,
        sides: int = 8) -> None:
    """Vertical cylinder (or frustum) with caps."""
    r1 = r0 if r1 is None else r1
    g.tube(m, (x, y, z0), (x, y, z1), [(0, r0, r0), (1, r1, r1)], [W, W], mat, sides=sides, phase=math.pi / sides)


def disc_y(m: Mesh, x: float, y0: float, y1: float, z: float, r: float, mat: str, sides: int = 12) -> None:
    """A round disc facing -Y (clocks, speaker cones): from y0 (back) to y1 (front, smaller y)."""
    g.tube(m, (x, y0, z), (x, y1, z), [(0, r, r), (1, r, r)], [W, W], mat, sides=sides)


def ball(m: Mesh, c: Vec, r: float, mat: str) -> None:
    g.icosahedron(m, c, (r, r, r), W, mat)


# ---------------------------------------------------------------- shell and finishes


def shell_floor(mat: str = "floor") -> Mesh:
    m = Mesh("shell_floor")
    bx(m, -HALF, HALF, -HALF, HALF, -0.1, 0.0, mat)
    return m


def _wall_slabs(m: Mesh, mat: str) -> None:
    t = WALL_THICKNESS
    bx(m, -HALF - t, HALF, HALF, HALF + t, -0.1, WALL_HEIGHT, mat)  # back wall
    bx(m, -HALF - t, -HALF, -HALF, HALF, -0.1, WALL_HEIGHT, mat)  # left wall


def shell_walls(mat: str = "wall") -> Mesh:
    m = Mesh("shell_walls")
    _wall_slabs(m, mat)
    return m


def shell_trim() -> Mesh:
    m = Mesh("shell_trim")
    t = WALL_THICKNESS
    bx(m, -HALF, HALF, HALF - 0.02, HALF, 0.0, 0.1, "trim")  # baseboards
    bx(m, -HALF, -HALF + 0.02, -HALF, HALF - 0.02, 0.0, 0.1, "trim")
    bx(m, -HALF - t - 0.02, HALF + 0.02, HALF - 0.02, HALF + t + 0.02, WALL_HEIGHT, WALL_HEIGHT + 0.05, "trim")
    bx(m, -HALF - t - 0.02, -HALF + 0.02, -HALF - 0.02, HALF - 0.02, WALL_HEIGHT, WALL_HEIGHT + 0.05, "trim")
    return m


def walls_paint() -> Mesh:
    m = Mesh("walls")
    _wall_slabs(m, "wall")
    # A chair rail: a second color at 0.9 m on both walls (a shape change, not only a color).
    plate(m, -HALF, HALF, HALF - 0.015, HALF, 0.86, 0.94, "accent", "+y")
    plate(m, -HALF, -HALF + 0.015, -HALF, HALF - 0.015, 0.86, 0.94, "accent", "-x")
    return m


def walls_stripes() -> Mesh:
    m = Mesh("walls")
    _wall_slabs(m, "wall")
    for i in range(8):
        x0 = -HALF + 0.125 + i * 0.5
        plate(m, x0, x0 + 0.25, HALF - 0.006, HALF, 0.1, WALL_HEIGHT, "stripe", "+y")
        y0 = -HALF + 0.125 + i * 0.5
        plate(m, -HALF, -HALF + 0.006, y0, min(y0 + 0.25, HALF - 0.006), 0.1, WALL_HEIGHT, "stripe", "-x")
    return m


def walls_brick() -> Mesh:
    m = Mesh("walls")
    _wall_slabs(m, "mortar")
    rows = int(WALL_HEIGHT / 0.25)
    for r in range(rows):
        z0 = r * 0.25 + 0.015
        shift = 0.0 if r % 2 == 0 else 0.25
        for c in range(8):
            a = -HALF + shift + c * 0.5 + 0.015
            b = a + 0.47
            if b > HALF - 0.01:
                continue
            mat = "brick" if (r * 3 + c) % 4 else "brick_b"
            plate(m, a, b, HALF - 0.012, HALF, z0, z0 + 0.22, mat, "+y")
            plate(m, -HALF, -HALF + 0.012, -b + 0.0, -a, z0, z0 + 0.22, mat, "-x")
    return m


def walls_neon() -> Mesh:
    m = Mesh("walls")
    _wall_slabs(m, "wall")
    for z, mat in ((0.55, "neon_a"), (1.95, "neon_b")):
        plate(m, -HALF, HALF, HALF - 0.02, HALF, z, z + 0.05, mat, "+y")
        plate(m, -HALF, -HALF + 0.02, -HALF, HALF - 0.02, z, z + 0.05, mat, "-x")
    for x in (-1.0, 1.0):
        plate(m, x - 0.025, x + 0.025, HALF - 0.02, HALF, 0.6, 1.95, "neon_a", "+y")
        plate(m, -HALF, -HALF + 0.02, -x - 0.025, -x + 0.025, 0.6, 1.95, "neon_b", "-x")
    return m


def floor_planks() -> Mesh:
    m = Mesh("floor")
    bx(m, -HALF, HALF, -HALF, HALF, -0.1, -0.006, "floor")
    for i in range(16):
        y0 = -HALF + i * 0.25
        cut = -HALF + (0.6 + 1.3 * ((i * 7) % 5)) % 3.4 + 0.3
        mat = "plank_a" if i % 2 == 0 else "plank_b"
        plate(m, -HALF, cut - 0.005, y0 + 0.004, y0 + 0.246, -0.006, 0.0, mat, "-z")
        plate(m, cut + 0.005, HALF, y0 + 0.004, y0 + 0.246, -0.006, 0.0, "plank_b" if mat == "plank_a" else "plank_a", "-z")
    return m


def floor_checker() -> Mesh:
    m = Mesh("floor")
    bx(m, -HALF, HALF, -HALF, HALF, -0.1, -0.004, "floor")
    for cx in range(8):
        for cy in range(8):
            x0, y0 = -HALF + cx * CELL, -HALF + cy * CELL
            plate(m, x0 + 0.004, x0 + CELL - 0.004, y0 + 0.004, y0 + CELL - 0.004, -0.004, 0.0,
                  "tile_a" if (cx + cy) % 2 == 0 else "tile_b", "-z")
    return m


def floor_court() -> Mesh:
    m = Mesh("floor")
    bx(m, -HALF, HALF, -HALF, HALF, -0.1, 0.0, "floor")
    z0, z1 = 0.0, 0.003
    # Key (lane) from the back wall, free-throw circle, three-point arc, center logo.
    plate(m, -0.75, 0.75, 0.5, HALF, z0, z1, "paint", "-z")
    for x in (-0.79, 0.75):
        plate(m, x, x + 0.04, 0.46, HALF, z0, z1 + 0.001, "line", "-z")
    plate(m, -0.79, 0.79, 0.46, 0.5, z0, z1 + 0.001, "line", "-z")
    circle = [(0.55 * math.cos(a), 0.5 + 0.55 * math.sin(a), 0.002) for a in [math.pi + i * math.pi / 8 for i in range(9)]]
    g.path_tube(m, circle, 0.018, W, "line", sides=4)
    arc = [(1.75 * math.cos(a), HALF + 1.75 * math.sin(a), 0.002) for a in [math.pi + i * math.pi / 12 for i in range(13)]]
    g.path_tube(m, arc, 0.018, W, "line", sides=4)
    g.tube(m, (0, -1.0, 0.0), (0, -1.0, 0.004), [(0, 0.45, 0.45), (1, 0.45, 0.45)], [W, W], "accent", sides=16)
    g.tube(m, (0, -1.0, 0.004), (0, -1.0, 0.007), [(0, 0.3, 0.3), (1, 0.3, 0.3)], [W, W], "paint", sides=16)
    return m


# ---------------------------------------------------------------- furniture (floor)


def beanbag() -> Mesh:
    m = Mesh("beanbag")
    g.ellipsoid(m, (0, 0, 0.24), (0.43, 0.42, 0.24), W, "primary", lon=10, lat=6)
    g.ellipsoid(m, (0, 0.17, 0.42), (0.36, 0.2, 0.2), W, "secondary", lon=10, lat=5)
    return m


def _legs(m: Mesh, xs: Sequence[float], ys: Sequence[float], h: float, mat: str, r: float = 0.03) -> None:
    for x in xs:
        for y in ys:
            cyl(m, x, y, 0.0, h, r, mat, sides=6)


def sofa() -> Mesh:
    m = Mesh("sofa")
    bx(m, -0.9, 0.9, -0.42, 0.38, 0.1, 0.38, "primary")
    bx(m, -0.9, 0.9, 0.2, 0.46, 0.38, 0.86, "primary")
    for s in (-1, 1):
        x0, x1 = sorted((s * 0.8, s * 0.98))
        bx(m, x0, x1, -0.44, 0.46, 0.1, 0.6, "secondary")
    for x0, x1 in ((-0.79, -0.01), (0.01, 0.79)):
        bx(m, x0, x1, -0.41, 0.2, 0.38, 0.5, "accent")
    g.oriented_box(m, (-0.55, 0.12, 0.62), (g.norm((1, 0, 0.2)), (0, 1, 0), g.norm((-0.2, 0, 1))), (0.15, 0.05, 0.13), W, "secondary")
    _legs(m, (-0.85, 0.85), (-0.38, 0.4), 0.1, "trim")
    return m


def armchair() -> Mesh:
    m = Mesh("armchair")
    bx(m, -0.36, 0.36, -0.4, 0.3, 0.1, 0.36, "primary")
    bx(m, -0.36, 0.36, 0.22, 0.46, 0.36, 0.86, "primary")
    for x0, x1 in ((-0.47, -0.36), (0.36, 0.47)):
        bx(m, x0, x1, -0.42, 0.46, 0.1, 0.58, "secondary")
    bx(m, -0.35, 0.35, -0.39, 0.22, 0.36, 0.47, "accent")
    _legs(m, (-0.42, 0.42), (-0.37, 0.41), 0.1, "trim")
    return m


def coffee_table() -> Mesh:
    m = Mesh("coffee_table")
    bx(m, -0.46, 0.46, -0.21, 0.21, 0.36, 0.42, "primary")
    bx(m, -0.4, 0.4, -0.17, 0.17, 0.1, 0.13, "primary")
    _legs(m, (-0.41, 0.41), (-0.16, 0.16), 0.36, "trim", r=0.025)
    bx(m, -0.25, 0.0, -0.12, 0.08, 0.42, 0.445, "accent")
    ball(m, (0.25, 0.0, 0.5), 0.075, "secondary")
    return m


def bed() -> Mesh:
    m = Mesh("bed")
    bx(m, -0.72, 0.72, -0.96, 0.9, 0.08, 0.28, "trim")
    bx(m, -0.68, 0.68, -0.92, 0.86, 0.28, 0.46, "secondary")
    bx(m, -0.71, 0.71, -0.95, 0.25, 0.3, 0.5, "primary")
    bx(m, -0.71, 0.71, 0.1, 0.25, 0.5, 0.52, "accent")  # folded edge of the blanket
    for x0, x1 in ((-0.6, -0.04), (0.04, 0.6)):
        bx(m, x0, x1, 0.45, 0.8, 0.46, 0.57, "secondary")
    bx(m, -0.73, 0.73, 0.88, 0.97, 0.08, 1.0, "trim")
    bx(m, -0.6, 0.6, 0.86, 0.88, 0.62, 0.9, "accent")
    _legs(m, (-0.67, 0.67), (-0.91, 0.85), 0.08, "trim", r=0.035)
    return m


def desk() -> Mesh:
    m = Mesh("desk")
    bx(m, -0.72, 0.72, -0.45, 0.45, 0.72, 0.77, "primary")
    bx(m, -0.7, -0.3, -0.42, 0.42, 0.0, 0.72, "secondary")
    for z0 in (0.06, 0.3, 0.52):
        bx(m, -0.68, -0.32, -0.43, -0.42, z0, z0 + 0.18, "secondary")
        bx(m, -0.55, -0.45, -0.445, -0.43, z0 + 0.12, z0 + 0.14, "metal")
    bx(m, 0.64, 0.7, -0.42, 0.42, 0.0, 0.72, "secondary")
    bx(m, -0.1, 0.3, -0.25, 0.02, 0.77, 0.79, "metal")
    bx(m, -0.1, 0.3, 0.02, 0.04, 0.79, 1.03, "metal")
    bx(m, -0.08, 0.28, 0.015, 0.02, 0.81, 1.01, "screen")
    cyl(m, 0.5, 0.15, 0.77, 0.9, 0.04, "accent", sides=6)  # a mug
    return m


def gaming_chair() -> Mesh:
    m = Mesh("gaming_chair")
    for i in range(5):
        a = 2 * math.pi * i / 5 + math.pi / 2
        g.oriented_box(m, (0.11 * math.cos(a), 0.11 * math.sin(a), 0.04),
                       ((math.cos(a), math.sin(a), 0), (-math.sin(a), math.cos(a), 0), (0, 0, 1)), (0.11, 0.02, 0.02), W, "metal")
        ball(m, (0.2 * math.cos(a), 0.2 * math.sin(a), 0.025), 0.025, "metal")
    cyl(m, 0, 0, 0.06, 0.4, 0.03, "metal", sides=6)
    bx(m, -0.22, 0.22, -0.22, 0.18, 0.4, 0.5, "primary")
    bx(m, -0.2, 0.2, 0.16, 0.24, 0.5, 1.15, "primary")
    bx(m, -0.05, 0.05, 0.145, 0.16, 0.55, 1.1, "secondary")
    bx(m, -0.12, 0.12, 0.13, 0.16, 0.98, 1.08, "secondary")
    for s in (-1, 1):
        x0, x1 = sorted((s * 0.215, s * 0.24))
        bx(m, x0, x1, -0.15, 0.1, 0.5, 0.66, "secondary")
        bx(m, x0, x1, -0.18, 0.12, 0.66, 0.69, "primary")
    return m


BOOKS = [(0.06, 0.26, "book_a"), (0.05, 0.3, "book_b"), (0.07, 0.24, "book_c"), (0.04, 0.28, "book_a"),
         (0.06, 0.22, "book_b"), (0.05, 0.27, "book_c"), (0.08, 0.25, "book_a")]


def bookshelf() -> Mesh:
    m = Mesh("bookshelf")
    for x0, x1 in ((-0.48, -0.45), (0.45, 0.48)):
        bx(m, x0, x1, -0.22, 0.22, 0.0, 1.6, "primary")
    bx(m, -0.45, 0.45, 0.19, 0.22, 0.0, 1.6, "primary")
    shelves = (0.02, 0.42, 0.82, 1.22, 1.57)
    for z in shelves:
        bx(m, -0.45, 0.45, -0.22, 0.19, z, z + 0.03, "primary")
    for s, z in enumerate(shelves[:3]):
        x = -0.43 + 0.04 * s
        for i, (w, h, mat) in enumerate(BOOKS[s:] + BOOKS[:s]):
            if x + w > 0.43:
                break
            bx(m, x, x + w - 0.004, -0.15, 0.16, z + 0.03, z + 0.03 + h, mat)
            x += w
    ball(m, (0.25, 0.0, 1.32), 0.07, "book_b")
    return m


def floor_lamp() -> Mesh:
    m = Mesh("floor_lamp")
    cyl(m, 0, 0, 0.0, 0.04, 0.16, "metal", sides=10)
    cyl(m, 0, 0, 0.04, 1.4, 0.018, "metal", sides=6)
    cyl(m, 0, 0, 1.33, 1.62, 0.21, "primary", r1=0.12, sides=10)
    ball(m, (0, 0, 1.3), 0.05, "secondary")
    return m


def potted_plant() -> Mesh:
    m = Mesh("potted_plant")
    cyl(m, 0, 0, 0.0, 0.32, 0.15, "primary", r1=0.19, sides=10)
    cyl(m, 0, 0, 0.32, 0.34, 0.2, "secondary", sides=10)
    g.icosahedron(m, (0, 0, 0.58), (0.17, 0.17, 0.22), W, "leaf_b")
    for i in range(6):
        a = 2 * math.pi * i / 6
        tip = (0.2 * math.cos(a), 0.2 * math.sin(a), 0.85 + 0.07 * (i % 2))
        g.cone(m, (0.05 * math.cos(a), 0.05 * math.sin(a), 0.4), tip, 0.06, W, "leaf_a", sides=4)
    return m


def cactus() -> Mesh:
    m = Mesh("cactus")
    bx(m, -0.17, 0.17, -0.17, 0.17, 0.0, 0.25, "primary")
    bx(m, -0.18, 0.18, -0.18, 0.18, 0.25, 0.28, "secondary")
    cyl(m, 0, 0, 0.28, 1.05, 0.09, "cactus", sides=8)
    ball(m, (0, 0, 1.05), 0.088, "cactus")
    g.path_tube(m, [(0.08, 0, 0.58), (0.19, 0, 0.6), (0.2, 0, 0.84)], 0.045, W, "cactus", sides=6)
    g.path_tube(m, [(-0.08, 0, 0.45), (-0.18, 0, 0.47), (-0.19, 0, 0.66)], 0.04, W, "cactus", sides=6)
    ball(m, (0, 0, 1.15), 0.04, "flower")
    return m


def hoops_arcade() -> Mesh:
    m = Mesh("hoops_arcade")
    bx(m, -0.46, 0.46, -0.72, 0.55, 0.0, 0.85, "primary")
    bx(m, -0.43, 0.43, -0.69, 0.55, 0.85, 0.88, "secondary")
    for x0, x1 in ((-0.48, -0.44), (0.44, 0.48)):
        bx(m, x0, x1, -0.72, 0.72, 0.85, 1.25, "neon")
    bx(m, -0.46, 0.46, 0.55, 0.72, 0.0, 2.2, "primary")
    bx(m, -0.36, 0.36, 0.5, 0.55, 1.45, 1.95, "secondary")
    bx(m, -0.12, 0.12, 0.49, 0.5, 1.5, 1.66, "neon")
    g.path_tube(m, [(0.13 * math.cos(a), 0.36 + 0.13 * math.sin(a), 1.5) for a in [i * math.pi / 5 for i in range(10)]],
                0.012, W, "metal", sides=4, closed=True)
    bx(m, -0.4, 0.4, 0.52, 0.55, 2.0, 2.15, "screen")
    bx(m, -0.47, 0.47, -0.73, -0.72, 0.4, 0.48, "neon")
    for x in (-0.22, 0.0, 0.22):
        ball(m, (x, -0.55, 0.98), 0.1, "ball")
    return m


def trophy_stand() -> Mesh:
    m = Mesh("trophy_stand")
    bx(m, -0.18, 0.18, -0.18, 0.18, 0.0, 0.7, "primary")
    bx(m, -0.1, 0.1, -0.186, -0.18, 0.45, 0.55, "metal")
    cyl(m, 0, 0, 0.7, 0.75, 0.08, "metal", sides=8)
    cyl(m, 0, 0, 0.75, 0.86, 0.025, "metal", sides=6)
    cyl(m, 0, 0, 0.86, 1.08, 0.055, "metal", r1=0.12, sides=10)
    for s in (-1, 1):
        g.path_tube(m, [(s * 0.1, 0, 1.04), (s * 0.165, 0, 1.0), (s * 0.15, 0, 0.92), (s * 0.075, 0, 0.9)], 0.012, W,
                    "metal", sides=4)
    return m


def ball_rack() -> Mesh:
    m = Mesh("ball_rack")
    for x in (-0.44, 0.44):
        for y in (-0.15, 0.15):
            cyl(m, x, y, 0.0, 0.78, 0.02, "metal", sides=6)
    for z in (0.3, 0.68):
        for y in (-0.13, 0.13):
            g.path_tube(m, [(-0.44, y, z), (0.44, y, z)], 0.015, W, "metal", sides=4)
    for i, x in enumerate((-0.3, -0.1, 0.1, 0.3)):
        ball(m, (x, 0, 0.41), 0.1, "ball")
        if i < 3:
            ball(m, (x + 0.1, 0, 0.79), 0.1, "ball")
    return m


def tv_stand() -> Mesh:
    m = Mesh("tv_stand")
    bx(m, -0.72, 0.72, -0.22, 0.22, 0.08, 0.5, "primary")
    for x0 in (-0.7, 0.0):
        bx(m, x0 + 0.02, x0 + 0.68, -0.226, -0.22, 0.12, 0.46, "secondary")
    _legs(m, (-0.66, 0.66), (-0.16, 0.16), 0.08, "metal", r=0.025)
    bx(m, -0.16, 0.16, -0.08, 0.08, 0.5, 0.53, "metal")
    bx(m, -0.04, 0.04, 0.0, 0.04, 0.53, 0.64, "metal")
    bx(m, -0.64, 0.64, 0.0, 0.05, 0.62, 1.34, "metal")
    bx(m, -0.6, 0.6, -0.005, 0.0, 0.66, 1.3, "screen")
    return m


def speaker_tower() -> Mesh:
    m = Mesh("speaker_tower")
    bx(m, -0.19, 0.19, -0.19, 0.19, 0.0, 1.1, "primary")
    for z, r in ((0.32, 0.13), (0.66, 0.1), (0.93, 0.05)):
        disc_y(m, 0, -0.19, -0.2, z, r + 0.015, "trim")
        disc_y(m, 0, -0.2, -0.21, z, r, "secondary")
        ball(m, (0, -0.21, z), r * 0.35, "accent")
    return m


def round_rug() -> Mesh:
    m = Mesh("round_rug")
    g.tube(m, (0, 0, 0.0), (0, 0, 0.012), [(0, 0.72, 0.72), (1, 0.72, 0.72)], [W, W], "primary", sides=16)
    g.tube(m, (0, 0, 0.012), (0, 0, 0.016), [(0, 0.5, 0.5), (1, 0.5, 0.5)], [W, W], "secondary", sides=16)
    g.tube(m, (0, 0, 0.016), (0, 0, 0.02), [(0, 0.25, 0.25), (1, 0.25, 0.25)], [W, W], "accent", sides=16)
    return m


def striped_rug() -> Mesh:
    m = Mesh("striped_rug")
    bx(m, -0.96, 0.96, -0.46, 0.46, 0.0, 0.012, "primary")
    for x in (-0.7, -0.3, 0.1, 0.5):
        plate(m, x, x + 0.2, -0.46, 0.46, 0.012, 0.016, "secondary", "-z")
    for x in (-0.98, 0.96):
        plate(m, x, x + 0.02, -0.44, 0.44, 0.0, 0.008, "accent", "-z")
    return m


# ---------------------------------------------------------------- wall decor


def poster() -> Mesh:
    m = Mesh("poster")
    bx(m, -0.45, 0.45, -0.03, 0.0, 0.05, 0.95, "trim")
    bx(m, -0.4, 0.4, -0.035, -0.03, 0.1, 0.9, "primary")
    disc_y(m, 0, -0.035, -0.04, 0.58, 0.22, "ball", sides=16)
    g.path_tube(m, [(-0.22, -0.043, 0.58), (0.22, -0.043, 0.58)], 0.008, W, "ink", sides=4)
    g.path_tube(m, [(0, -0.043, 0.36), (0, -0.043, 0.8)], 0.008, W, "ink", sides=4)
    bx(m, -0.3, 0.3, -0.04, -0.035, 0.16, 0.26, "secondary")
    return m


def wall_shelf() -> Mesh:
    m = Mesh("wall_shelf")
    bx(m, -0.46, 0.46, -0.22, 0.0, 0.22, 0.26, "primary")
    for x in (-0.3, 0.3):
        g.oriented_box(m, (x, -0.08, 0.17), ((1, 0, 0), g.norm((0, 1, 1)), g.norm((0, -1, 1))), (0.015, 0.1, 0.01), W,
                       "metal")
        bx(m, x - 0.015, x + 0.015, -0.02, 0.0, 0.08, 0.22, "metal")
    for i, (w, h) in enumerate(((0.05, 0.2), (0.04, 0.18), (0.06, 0.22))):
        x = -0.38 + 0.055 * i
        bx(m, x, x + w - 0.004, -0.18, -0.03, 0.26, 0.26 + h, "accent" if i % 2 else "secondary")
    cyl(m, 0.25, -0.11, 0.26, 0.33, 0.055, "secondary", r1=0.07, sides=8)
    g.icosahedron(m, (0.25, -0.11, 0.4), (0.085, 0.085, 0.08), W, "leaf")
    return m


def wall_clock() -> Mesh:
    m = Mesh("wall_clock")
    disc_y(m, 0, 0.0, -0.04, 0.25, 0.21, "primary", sides=16)
    disc_y(m, 0, -0.04, -0.045, 0.25, 0.18, "secondary", sides=16)
    for i in range(12):
        a = 2 * math.pi * i / 12
        bx(m, 0.15 * math.cos(a) - 0.008, 0.15 * math.cos(a) + 0.008, -0.05, -0.045, 0.25 + 0.15 * math.sin(a) - 0.008,
           0.25 + 0.15 * math.sin(a) + 0.008, "ink")
    g.path_tube(m, [(0, -0.052, 0.25), (0.0, -0.052, 0.37)], 0.008, W, "ink", sides=4)
    g.path_tube(m, [(0, -0.056, 0.25), (0.08, -0.056, 0.2)], 0.008, W, "accent", sides=4)
    return m


def mini_backboard() -> Mesh:
    m = Mesh("mini_backboard")
    bx(m, -0.45, 0.45, -0.04, 0.0, 0.3, 0.95, "secondary")
    for (x0, x1, z0, z1) in ((-0.45, 0.45, 0.3, 0.33), (-0.45, 0.45, 0.92, 0.95), (-0.45, -0.42, 0.3, 0.95), (0.42, 0.45, 0.3, 0.95)):
        bx(m, x0, x1, -0.045, -0.04, z0, z1, "primary")
    for (x0, x1, z0, z1) in ((-0.15, 0.15, 0.42, 0.44), (-0.15, 0.15, 0.62, 0.64), (-0.15, -0.13, 0.42, 0.64), (0.13, 0.15, 0.42, 0.64)):
        bx(m, x0, x1, -0.045, -0.04, z0, z1, "primary")
    bx(m, -0.04, 0.04, -0.06, -0.04, 0.36, 0.42, "metal")
    rim = [(0.12 * math.cos(a), -0.165 + 0.12 * math.sin(a), 0.4) for a in [i * math.pi / 5 for i in range(10)]]
    g.path_tube(m, rim, 0.012, W, "metal", sides=4, closed=True)
    for i, (x, y, _z) in enumerate(rim):
        if i % 2 == 0:
            g.path_tube(m, [(x, y, 0.4), (x * 0.55, -0.165 + (y + 0.165) * 0.55, 0.2)], 0.005, W, "net", sides=3)
    return m


def neon_bolt() -> Mesh:
    m = Mesh("neon_bolt")
    bx(m, -0.42, 0.42, -0.02, 0.0, 0.08, 0.92, "primary")
    bolt = [(-0.1, -0.05, 0.85), (0.15, -0.05, 0.85), (0.0, -0.05, 0.56), (0.18, -0.05, 0.56), (-0.12, -0.05, 0.14),
            (0.0, -0.05, 0.45), (-0.17, -0.05, 0.45), (-0.1, -0.05, 0.85)]
    g.path_tube(m, bolt, 0.022, W, "neon_a", sides=4)
    ring = [(0.33 * math.cos(a), -0.04, 0.5 + 0.38 * math.sin(a)) for a in [i * math.pi / 8 for i in range(16)]]
    g.path_tube(m, ring, 0.014, W, "neon_b", sides=4, closed=True)
    return m


# ---------------------------------------------------------------- catalog


@dataclass(frozen=True)
class RoomAsset:
    """One .glb file. footprint: (w, d) cells on the floor, (w, h) cells on a wall; None for finishes."""

    id: str
    slot: str
    build: Callable[[], Mesh]
    mats: Dict[str, Material]
    footprint: Optional[Tuple[int, int]] = None
    layer: Optional[str] = None  # floor items: "rug" or "object"
    version: int = 1


@dataclass(frozen=True)
class RoomItem:
    sku: str
    name: str
    price: int
    rarity: str
    min_level: int
    asset: str
    asset_key: str
    colors: Dict[str, str] = field(default_factory=dict)


WOOD = "#b5835a"
DARK_WOOD = "#6f4e37"
METAL = "#9aa3b5"

SHELL_MATS = mats(floor="#d9c6a5", wall="#eef1f7", trim="#9aa3b5")

ROOM_ASSETS: List[RoomAsset] = [
    # Finishes: they replace the shell walls or the shell floor.
    RoomAsset("walls_paint", "room_wall", walls_paint, mats(wall="#bde0d0", accent="#5b8e7d")),
    RoomAsset("walls_stripes", "room_wall", walls_stripes, mats(wall="#f4f1de", stripe="#a8dadc")),
    RoomAsset("walls_brick", "room_wall", walls_brick, mats(mortar="#d8cfc4", brick="#b5523b", brick_b="#9c4130")),
    RoomAsset("walls_neon", "room_wall", walls_neon, mats(wall="#1d1b33", neon_a="#ff4fd8|#ff1fcf", neon_b="#4ff0ff|#00e1ff")),
    RoomAsset("floor_planks", "room_floor", floor_planks, mats(floor="#5e4330", plank_a="#c8935e", plank_b="#b27c4b")),
    RoomAsset("floor_checker", "room_floor", floor_checker, mats(floor="#2b2d42", tile_a="#f2f2f2", tile_b="#2b2d42")),
    RoomAsset("floor_court", "room_floor", floor_court,
              mats(floor="#e3b778", paint="#d1495b", line="#ffffff", accent="#2f6fed")),
    # Furniture (floor).
    RoomAsset("beanbag", "furniture", beanbag, mats(primary="#ff7a45", secondary="#ff9f6e"), (2, 2), "object"),
    RoomAsset("sofa", "furniture", sofa, mats(primary="#3d5a80", secondary="#293d5a", accent="#98c1d9", trim=DARK_WOOD),
              (4, 2), "object"),
    RoomAsset("armchair", "furniture", armchair, mats(primary="#e9c46a", secondary="#d4a72c", accent="#f6e3a1", trim=DARK_WOOD),
              (2, 2), "object"),
    RoomAsset("coffee_table", "furniture", coffee_table,
              mats(primary=WOOD, trim=DARK_WOOD, accent="#e63946", secondary="#ff8c42"), (2, 1), "object"),
    RoomAsset("bed", "furniture", bed, mats(primary="#6c63ff", secondary="#f8f9fa", accent="#ffc83d", trim=WOOD),
              (3, 4), "object"),
    RoomAsset("desk", "furniture", desk,
              mats(primary=WOOD, secondary="#f1faee", metal=METAL, screen="#1d3557", accent="#e63946"), (3, 2), "object"),
    RoomAsset("gaming_chair", "furniture", gaming_chair, mats(primary="#1c1c22", secondary="#e63946", metal="#5c6370"),
              (1, 1), "object"),
    RoomAsset("bookshelf", "furniture", bookshelf,
              mats(primary=WOOD, book_a="#2f6fed", book_b="#2ee59d", book_c="#ef476f"), (2, 1), "object"),
    RoomAsset("floor_lamp", "furniture", floor_lamp, mats(primary="#ffd166", secondary="#fff3c4", metal="#2b2d42"),
              (1, 1), "object"),
    RoomAsset("potted_plant", "furniture", potted_plant,
              mats(primary="#c8553d", secondary="#5b3a29", leaf_a="#2a9d8f", leaf_b="#3fb27f"), (1, 1), "object"),
    RoomAsset("cactus", "furniture", cactus,
              mats(primary="#f4a259", secondary="#bc6c25", cactus="#4c956c", flower="#ff70a6"), (1, 1), "object"),
    RoomAsset("hoops_arcade", "furniture", hoops_arcade,
              mats(primary="#2a1458", secondary="#f8f9fa", neon="#7df9ff|#29d5ff", metal="#ff6b1a",
                   screen="#fff3a3|#ffe14d", ball="#e76f51"), (2, 3), "object"),
    RoomAsset("trophy_stand", "furniture", trophy_stand, mats(primary="#3c2a1e", metal="#f2c94c"), (1, 1), "object"),
    RoomAsset("ball_rack", "furniture", ball_rack, mats(metal="#2b2d42", ball="#e76f51"), (2, 1), "object"),
    RoomAsset("tv_stand", "furniture", tv_stand,
              mats(primary="#2b2d42", secondary="#3d405b", metal="#1c1c22", screen="#26408b"), (3, 1), "object"),
    RoomAsset("speaker_tower", "furniture", speaker_tower,
              mats(primary="#1c1c22", secondary="#3d3d46", trim="#5c6370", accent="#ffc83d"), (1, 1), "object"),
    RoomAsset("round_rug", "furniture", round_rug, mats(primary="#f28482", secondary="#f6bd60", accent="#84a59d"),
              (3, 3), "rug"),
    RoomAsset("striped_rug", "furniture", striped_rug, mats(primary="#264653", secondary="#e9c46a", accent="#f4a261"),
              (4, 2), "rug"),
    # Wall decor.
    RoomAsset("poster", "wall_decor", poster,
              mats(trim="#1c1c22", primary="#2f6fed", ball="#ff8c42", ink="#1c1c22", secondary="#ffffff"), (2, 2)),
    RoomAsset("wall_shelf", "wall_decor", wall_shelf,
              mats(primary=WOOD, metal="#2b2d42", accent="#ef476f", secondary="#f4f1de", leaf="#3fb27f"), (2, 1)),
    RoomAsset("wall_clock", "wall_decor", wall_clock,
              mats(primary="#2f6fed", secondary="#ffffff", ink="#1c1c22", accent="#e63946"), (1, 1)),
    RoomAsset("mini_backboard", "wall_decor", mini_backboard,
              mats(secondary="#f8f9fa", primary="#e63946", metal="#ff6b1a", net="#ffffff"), (2, 2)),
    RoomAsset("neon_bolt", "wall_decor", neon_bolt,
              mats(primary="#14112b", neon_a="#ffe14d|#ffd400", neon_b="#ff4fd8|#ff1fcf"), (2, 2)),
]
ROOM_ASSET_BY_ID = {a.id: a for a in ROOM_ASSETS}

ROOM_ITEMS: List[RoomItem] = [
    # Walls and floors (finishes).
    RoomItem("walls_mint", "Mint Walls", 300, "common", 1, "walls_paint", "room/walls_mint"),
    RoomItem("walls_sunset", "Sunset Walls", 300, "common", 1, "walls_paint", "room/walls_sunset",
             colors={"wall": "#ffcf9f", "accent": "#e76f51"}),
    RoomItem("walls_stripes", "Striped Walls", 500, "common", 2, "walls_stripes", "room/walls_stripes"),
    RoomItem("walls_brick", "Brick Walls", 1500, "rare", 3, "walls_brick", "room/walls_brick"),
    RoomItem("walls_neon_night", "Neon Night Walls", 7000, "epic", 8, "walls_neon", "room/walls_neon_night"),
    RoomItem("floor_oak", "Oak Floor", 400, "common", 1, "floor_planks", "room/floor_oak"),
    RoomItem("floor_checker", "Checker Tiles", 450, "common", 2, "floor_checker", "room/floor_checker"),
    RoomItem("floor_court", "Court Floor", 2000, "rare", 4, "floor_court", "room/floor_court"),
    # Furniture.
    RoomItem("furn_beanbag", "Beanbag", 350, "common", 1, "beanbag", "room/beanbag"),
    RoomItem("furn_sofa", "Comfy Sofa", 750, "common", 2, "sofa", "room/sofa"),
    RoomItem("furn_armchair", "Armchair", 550, "common", 1, "armchair", "room/armchair"),
    RoomItem("furn_coffee_table", "Coffee Table", 400, "common", 1, "coffee_table", "room/coffee_table"),
    RoomItem("furn_bed", "Cozy Bed", 800, "common", 2, "bed", "room/bed"),
    RoomItem("furn_desk", "Study Desk", 650, "common", 2, "desk", "room/desk"),
    RoomItem("furn_gaming_chair", "Gaming Chair", 1800, "rare", 4, "gaming_chair", "room/gaming_chair"),
    RoomItem("furn_bookshelf", "Bookshelf", 600, "common", 1, "bookshelf", "room/bookshelf"),
    RoomItem("furn_floor_lamp", "Floor Lamp", 300, "common", 1, "floor_lamp", "room/floor_lamp"),
    RoomItem("furn_potted_plant", "Potted Plant", 300, "common", 1, "potted_plant", "room/potted_plant"),
    RoomItem("furn_cactus", "Big Cactus", 350, "common", 1, "cactus", "room/cactus"),
    RoomItem("furn_hoops_arcade", "Hoops Arcade", 9000, "epic", 10, "hoops_arcade", "room/hoops_arcade"),
    RoomItem("furn_trophy_stand", "Trophy Stand", 2000, "rare", 5, "trophy_stand", "room/trophy_stand"),
    RoomItem("furn_ball_rack", "Ball Rack", 500, "common", 1, "ball_rack", "room/ball_rack"),
    RoomItem("furn_tv_stand", "TV Stand", 1600, "rare", 3, "tv_stand", "room/tv_stand"),
    RoomItem("furn_speaker_tower", "Speaker Tower", 1500, "rare", 3, "speaker_tower", "room/speaker_tower"),
    RoomItem("furn_round_rug", "Round Rug", 400, "common", 1, "round_rug", "room/round_rug"),
    RoomItem("furn_striped_rug", "Striped Rug", 450, "common", 1, "striped_rug", "room/striped_rug"),
    # Wall decor.
    RoomItem("decor_hoops_poster", "Hoops Poster", 300, "common", 1, "poster", "room/hoops_poster"),
    RoomItem("decor_wall_shelf", "Wall Shelf", 350, "common", 1, "wall_shelf", "room/wall_shelf"),
    RoomItem("decor_wall_clock", "Wall Clock", 300, "common", 1, "wall_clock", "room/wall_clock"),
    RoomItem("decor_mini_backboard", "Mini Backboard", 1800, "rare", 4, "mini_backboard", "room/mini_backboard"),
    RoomItem("decor_neon_bolt", "Neon Bolt", 6500, "epic", 7, "neon_bolt", "room/neon_bolt"),
]


def surfaces(slot: str) -> List[str]:
    return {"furniture": ["floor"], "wall_decor": ["wall_back", "wall_left"]}.get(slot, [])


# ---------------------------------------------------------------- placement (same rules as the API and the web)


def rotated(footprint: Tuple[int, int], rotation: int) -> Tuple[int, int]:
    w, d = footprint
    return (d, w) if rotation in (90, 270) else (w, d)


def place_floor(x: int, y: int, footprint: Tuple[int, int], rotation: int) -> Tuple[Vec, float]:
    """Blender location and Z rotation (radians) of a floor item at cell (x, y)."""
    w, d = rotated(footprint, rotation)
    return (-HALF + CELL * (x + w / 2), HALF - CELL * (y + d / 2), 0.0), math.radians(rotation)


def place_wall(surface: str, x: int, y: int, footprint: Tuple[int, int]) -> Tuple[Vec, float]:
    """Blender location and Z rotation of a wall item at column x, row y."""
    w, _h = footprint
    along = -HALF + CELL * (x + w / 2)
    if surface == "wall_back":
        return (along, HALF, CELL * y), 0.0
    return (-HALF, -along, CELL * y), math.radians(90)
