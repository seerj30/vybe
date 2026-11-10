"""Low-poly mesh primitives in plain Python (no Blender import).

All geometry of the VYBE avatar is made here, as lists of points and faces. Blender only turns the
result into objects, exports glTF and renders thumbnails. Plain Python with rounded coordinates keeps
the output the same on every run.

Conventions (Blender scene space): meters, Z up, the avatar faces -Y, origin at ground center.
Faces are counter-clockwise when seen from outside (outward normals).
"""

from __future__ import annotations

import math
from typing import Callable, Dict, List, Optional, Sequence, Tuple, Union

Vec = Tuple[float, float, float]
Weights = Dict[str, float]
WeightSpec = Union[str, Weights]

DIGITS = 5  # 0.01 mm: far below what a player can see, and it removes float noise between runs


def rnd(v: float) -> float:
    r = round(v, DIGITS)
    return 0.0 if r == 0 else r


def add(a: Vec, b: Vec) -> Vec:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def sub(a: Vec, b: Vec) -> Vec:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def mul(a: Vec, s: float) -> Vec:
    return (a[0] * s, a[1] * s, a[2] * s)


def dot(a: Vec, b: Vec) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a: Vec, b: Vec) -> Vec:
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a: Vec) -> Vec:
    n = math.sqrt(dot(a, a))
    return (a[0] / n, a[1] / n, a[2] / n)


def lerp(a: Vec, b: Vec, t: float) -> Vec:
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)


def mirror_x(p: Vec) -> Vec:
    return (-p[0], p[1], p[2])


def frame(d: Vec) -> Tuple[Vec, Vec]:
    """Two unit vectors u, v perpendicular to direction d, with u x v = d.

    For vertical and near-vertical directions u points to +X; otherwise u is horizontal. So the rings of
    a body part and of the garment around it always line up.
    """
    d = norm(d)
    ref: Vec = (1.0, 0.0, 0.0)
    if abs(d[2]) < 0.9:
        ref = cross(d, (0.0, 0.0, 1.0))
        if dot(ref, (1.0, 0.0, 0.0)) < 0 and abs(d[0]) < 0.9:
            ref = mul(ref, -1.0)
    u = norm(sub(ref, mul(d, dot(ref, d))))
    v = cross(d, u)
    return u, v


def as_weights(spec: WeightSpec) -> Weights:
    if isinstance(spec, str):
        return {spec: 1.0}
    total = sum(spec.values())
    return {k: v / total for k, v in spec.items() if v > 0}


def blend(a: WeightSpec, b: WeightSpec, t: float) -> Weights:
    """Weights between two bones (t = 0: all a, t = 1: all b)."""
    wa, wb = as_weights(a), as_weights(b)
    out: Weights = {}
    for k, v in wa.items():
        out[k] = out.get(k, 0.0) + v * (1 - t)
    for k, v in wb.items():
        out[k] = out.get(k, 0.0) + v * t
    return {k: round(v, 3) for k, v in out.items() if round(v, 3) > 0}


class Mesh:
    """One skinned part: points, per-point bone weights, faces and a material name per face."""

    def __init__(self, name: str) -> None:
        self.name = name
        self.verts: List[Vec] = []
        self.weights: List[Weights] = []
        self.faces: List[Tuple[int, ...]] = []
        self.mats: List[str] = []

    def vert(self, p: Vec, w: WeightSpec) -> int:
        self.verts.append((rnd(p[0]), rnd(p[1]), rnd(p[2])))
        self.weights.append(as_weights(w))
        return len(self.verts) - 1

    def face(self, idx: Sequence[int], mat: str) -> None:
        self.faces.append(tuple(idx))
        self.mats.append(mat)

    def merge(self, other: "Mesh") -> "Mesh":
        base = len(self.verts)
        self.verts.extend(other.verts)
        self.weights.extend(other.weights)
        self.faces.extend(tuple(i + base for i in f) for f in other.faces)
        self.mats.extend(other.mats)
        return self

    def triangles(self) -> int:
        return sum(len(f) - 2 for f in self.faces)

    def materials(self) -> List[str]:
        seen: List[str] = []
        for m in self.mats:
            if m not in seen:
                seen.append(m)
        return seen

    def mirrored(self, name: str, rename: Callable[[str], str]) -> "Mesh":
        """Mirror across X (left to right): points, bone names (via rename) and face order."""
        out = Mesh(name)
        out.verts = [(rnd(-x), y, z) for (x, y, z) in self.verts]
        out.weights = [{rename(k): v for k, v in w.items()} for w in self.weights]
        out.faces = [tuple(reversed(f)) for f in self.faces]
        out.mats = list(self.mats)
        return out

    def same_topology(self, other: "Mesh") -> bool:
        return len(self.verts) == len(other.verts) and self.faces == other.faces and self.mats == other.mats


# ---------------------------------------------------------------- rings and lofts

Ring = List[Vec]


def ring(center: Vec, d: Vec, rx: float, ry: float, sides: int, phase: float = 0.0) -> Ring:
    """Points of an ellipse around axis d, counter-clockwise when seen from +d."""
    u, v = frame(d)
    pts: Ring = []
    for i in range(sides):
        t = phase + 2 * math.pi * i / sides
        pts.append(add(center, add(mul(u, rx * math.cos(t)), mul(v, ry * math.sin(t)))))
    return pts


MatFn = Union[str, Callable[[int, int], str]]


def _mat(mat: MatFn, band: int, side: int) -> str:
    return mat if isinstance(mat, str) else mat(band, side)


def loft(
    mesh: Mesh,
    rings: Sequence[Ring],
    weights: Sequence[WeightSpec],
    mat: MatFn,
    cap_start: bool = False,
    cap_end: bool = False,
    cap_mat: Optional[str] = None,
) -> None:
    """Quads between consecutive rings (all rings the same size, along the ring axis direction)."""
    sides = len(rings[0])
    ids = [[mesh.vert(p, weights[r]) for p in pts] for r, pts in enumerate(rings)]
    for b in range(len(rings) - 1):
        lo, hi = ids[b], ids[b + 1]
        for i in range(sides):
            j = (i + 1) % sides
            mesh.face((lo[i], lo[j], hi[j], hi[i]), _mat(mat, b, i))
    cm = cap_mat or _mat(mat, 0, 0)
    if cap_start:
        c = mesh.vert(_centroid(rings[0]), weights[0])
        first = ids[0]
        for i in range(sides):
            mesh.face((c, first[(i + 1) % sides], first[i]), cm)
    if cap_end:
        c = mesh.vert(_centroid(rings[-1]), weights[-1])
        last = ids[-1]
        for i in range(sides):
            mesh.face((c, last[i], last[(i + 1) % sides]), cm)


def _centroid(pts: Sequence[Vec]) -> Vec:
    n = float(len(pts))
    return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n, sum(p[2] for p in pts) / n)


def tube(
    mesh: Mesh,
    start: Vec,
    end: Vec,
    radii: Sequence[Tuple[float, float, float]],
    weights: Sequence[WeightSpec],
    mat: MatFn,
    sides: int = 8,
    phase: float = 0.0,
    caps: Tuple[bool, bool] = (True, True),
    cap_mat: Optional[str] = None,
) -> None:
    """A tube from start to end. radii: (t along the axis 0..1, rx, ry) per ring."""
    d = sub(end, start)
    rings = [ring(lerp(start, end, t), d, rx, ry, sides, phase) for (t, rx, ry) in radii]
    loft(mesh, rings, weights, mat, caps[0], caps[1], cap_mat)


def box(
    mesh: Mesh,
    center: Vec,
    size: Vec,
    w: WeightSpec,
    mat: str,
    top_scale: Tuple[float, float] = (1.0, 1.0),
) -> None:
    """Axis-aligned box (optionally narrower at the top, for wedges and soles)."""
    hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2
    cx, cy, cz = center
    bot = [(cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz), (cx + hx, cy + hy, cz - hz), (cx - hx, cy + hy, cz - hz)]
    tx, ty = hx * top_scale[0], hy * top_scale[1]
    top = [(cx - tx, cy - ty, cz + hz), (cx + tx, cy - ty, cz + hz), (cx + tx, cy + ty, cz + hz), (cx - tx, cy + ty, cz + hz)]
    b = [mesh.vert(p, w) for p in bot]
    t = [mesh.vert(p, w) for p in top]
    mesh.face((b[0], b[3], b[2], b[1]), mat)  # bottom (-Z)
    mesh.face((t[0], t[1], t[2], t[3]), mat)  # top (+Z)
    mesh.face((b[0], b[1], t[1], t[0]), mat)  # front (-Y)
    mesh.face((b[1], b[2], t[2], t[1]), mat)  # +X
    mesh.face((b[2], b[3], t[3], t[2]), mat)  # back (+Y)
    mesh.face((b[3], b[0], t[0], t[3]), mat)  # -X


def oriented_box(mesh: Mesh, center: Vec, axes: Tuple[Vec, Vec, Vec], half: Vec, w: WeightSpec, mat: str) -> None:
    """Box with its own axes (a, b, c must form a right-handed set)."""
    a, b, c = axes
    corners = []
    for sz in (-1, 1):
        for sy, sx in ((-1, -1), (-1, 1), (1, 1), (1, -1)):
            corners.append(add(center, add(mul(a, sx * half[0]), add(mul(b, sy * half[1]), mul(c, sz * half[2])))))
    i = [mesh.vert(p, w) for p in corners]
    mesh.face((i[0], i[3], i[2], i[1]), mat)
    mesh.face((i[4], i[5], i[6], i[7]), mat)
    mesh.face((i[0], i[1], i[5], i[4]), mat)
    mesh.face((i[1], i[2], i[6], i[5]), mat)
    mesh.face((i[2], i[3], i[7], i[6]), mat)
    mesh.face((i[3], i[0], i[4], i[7]), mat)


def ellipsoid(
    mesh: Mesh,
    center: Vec,
    radii: Vec,
    w: WeightSpec,
    mat: MatFn,
    lon: int = 8,
    lat: int = 6,
    phase: float = 0.0,
) -> None:
    """Closed low-poly UV ellipsoid with poles on the Z axis."""
    rows: List[Ring] = []
    for r in range(1, lat):
        phi = math.pi * r / lat
        rows.append(_lat_ring(center, radii, phi, lon, phase))
    surface(mesh, rows, w, mat, top_pole=add(center, (0, 0, radii[2])), bottom_pole=add(center, (0, 0, -radii[2])))


def _lat_ring(center: Vec, radii: Vec, phi: float, lon: int, phase: float) -> Ring:
    pts: Ring = []
    for j in range(lon):
        th = phase + 2 * math.pi * j / lon
        pts.append(
            (
                center[0] + radii[0] * math.sin(phi) * math.cos(th),
                center[1] + radii[1] * math.sin(phi) * math.sin(th),
                center[2] + radii[2] * math.cos(phi),
            )
        )
    return pts


def surface(
    mesh: Mesh,
    rows: Sequence[Ring],
    w: Union[WeightSpec, Callable[[Vec], WeightSpec]],
    mat: MatFn,
    top_pole: Optional[Vec] = None,
    bottom_pole: Optional[Vec] = None,
) -> None:
    """Closed-around grid: rows from top to bottom, each counter-clockwise when seen from above."""
    wf = w if callable(w) else (lambda _p: w)
    n = len(rows[0])
    ids = [[mesh.vert(p, wf(p)) for p in row] for row in rows]
    if top_pole is not None:
        t = mesh.vert(top_pole, wf(top_pole))
        for j in range(n):
            mesh.face((t, ids[0][j], ids[0][(j + 1) % n]), _mat(mat, 0, j))
    for r in range(len(rows) - 1):
        a, b = ids[r], ids[r + 1]
        for j in range(n):
            k = (j + 1) % n
            mesh.face((a[j], b[j], b[k], a[k]), _mat(mat, r + 1, j))
    if bottom_pole is not None:
        s = mesh.vert(bottom_pole, wf(bottom_pole))
        last = ids[-1]
        for j in range(n):
            mesh.face((s, last[(j + 1) % n], last[j]), _mat(mat, len(rows), j))


def sheet(mesh: Mesh, rows: Sequence[Ring], w: Union[WeightSpec, Callable[[Vec], WeightSpec]], mat: MatFn) -> None:
    """Open grid (hair curtains, hoods, brims): rows from top to bottom, points in counter-clockwise order."""
    wf = w if callable(w) else (lambda _p: w)
    ids = [[mesh.vert(p, wf(p)) for p in row] for row in rows]
    for r in range(len(rows) - 1):
        a, b = ids[r], ids[r + 1]
        for j in range(len(a) - 1):
            mesh.face((a[j], b[j], b[j + 1], a[j + 1]), _mat(mat, r, j))


def cap_shell(
    mesh: Mesh,
    center: Vec,
    radii: Vec,
    cut: Callable[[float], float],
    w: WeightSpec,
    mat: MatFn,
    lon: int = 12,
    rows: int = 4,
    phase: float = 0.0,
) -> None:
    """Open dome (hair, caps): from the top pole down to polar angle cut(theta) at each longitude."""
    out: List[Ring] = []
    for r in range(1, rows + 1):
        s = r / rows
        row: Ring = []
        for j in range(lon):
            th = phase + 2 * math.pi * j / lon
            phi = cut(th) * s
            row.append(
                (
                    center[0] + radii[0] * math.sin(phi) * math.cos(th),
                    center[1] + radii[1] * math.sin(phi) * math.sin(th),
                    center[2] + radii[2] * math.cos(phi),
                )
            )
        out.append(row)
    surface(mesh, out, w, mat, top_pole=add(center, (0, 0, radii[2])))


def cone(mesh: Mesh, base: Vec, tip: Vec, r: float, w: WeightSpec, mat: str, sides: int = 5, phase: float = 0.0) -> None:
    pts = ring(base, sub(tip, base), r, r, sides, phase)
    ids = [mesh.vert(p, w) for p in pts]
    t = mesh.vert(tip, w)
    c = mesh.vert(base, w)
    for i in range(sides):
        j = (i + 1) % sides
        mesh.face((ids[i], ids[j], t), mat)
        mesh.face((c, ids[j], ids[i]), mat)


_ICO_T = (1 + math.sqrt(5)) / 2
_ICO_V = [(-1, _ICO_T, 0), (1, _ICO_T, 0), (-1, -_ICO_T, 0), (1, -_ICO_T, 0), (0, -1, _ICO_T), (0, 1, _ICO_T),
          (0, -1, -_ICO_T), (0, 1, -_ICO_T), (_ICO_T, 0, -1), (_ICO_T, 0, 1), (-_ICO_T, 0, -1), (-_ICO_T, 0, 1)]
_ICO_F = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
          (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
          (8, 6, 7), (9, 8, 1)]


def icosahedron(mesh: Mesh, center: Vec, radii: Vec, w: WeightSpec, mat: str) -> None:
    """20 triangles: curls, pompoms, gems."""
    n = math.sqrt(1 + _ICO_T * _ICO_T)
    ids = [mesh.vert((center[0] + radii[0] * x / n, center[1] + radii[1] * y / n, center[2] + radii[2] * z / n), w)
           for (x, y, z) in _ICO_V]
    for f in _ICO_F:
        mesh.face((ids[f[0]], ids[f[1]], ids[f[2]]), mat)


def path_tube(
    mesh: Mesh,
    points: Sequence[Vec],
    radius: float,
    w: Union[WeightSpec, Callable[[int], WeightSpec]],
    mat: str,
    sides: int = 4,
    closed: bool = False,
) -> None:
    """Thin tube along a polyline (straps, bands, mouth lines, headphone arcs)."""
    wf = w if callable(w) else (lambda _i: w)
    n = len(points)
    rings_: List[Ring] = []
    for i, p in enumerate(points):
        if closed:
            d = sub(points[(i + 1) % n], points[(i - 1) % n])
        else:
            d = sub(points[min(i + 1, n - 1)], points[max(i - 1, 0)])
        rings_.append(ring(p, d, radius, radius, sides, math.pi / sides))
    if closed:
        rings_.append(rings_[0])
    loft(mesh, rings_, [wf(i % n) for i in range(len(rings_))], mat, cap_start=not closed, cap_end=not closed)


def arc(center: Vec, radius_x: float, radius_z: float, start: float, end: float, steps: int, y: float) -> List[Vec]:
    """Points on an arc in a plane of constant y (angles in radians, 0 = +X, counter-clockwise toward +Z)."""
    out: List[Vec] = []
    for i in range(steps + 1):
        a = start + (end - start) * i / steps
        out.append((center[0] + radius_x * math.cos(a), y, center[2] + radius_z * math.sin(a)))
    return out


def ray_y(mesh: Mesh, x: float, z: float) -> float:
    """Smallest y where a ray along +Y at (x, z) hits the mesh (the front surface). Raises if none."""
    best: Optional[float] = None
    for f in mesh.faces:
        for k in range(1, len(f) - 1):
            a, b, c = mesh.verts[f[0]], mesh.verts[f[k]], mesh.verts[f[k + 1]]
            y = _ray_tri(x, z, a, b, c)
            if y is not None and (best is None or y < best):
                best = y
    if best is None:
        raise ValueError(f"no surface at x={x}, z={z} in {mesh.name}")
    return best


def _ray_tri(x: float, z: float, a: Vec, b: Vec, c: Vec) -> Optional[float]:
    # Barycentric test in the XZ plane, then the y of the plane at (x, z).
    d = (b[0] - a[0]) * (c[2] - a[2]) - (c[0] - a[0]) * (b[2] - a[2])
    if abs(d) < 1e-12:
        return None
    u = ((x - a[0]) * (c[2] - a[2]) - (c[0] - a[0]) * (z - a[2])) / d
    v = ((b[0] - a[0]) * (z - a[2]) - (x - a[0]) * (b[2] - a[2])) / d
    if u < -1e-9 or v < -1e-9 or u + v > 1 + 1e-9:
        return None
    return a[1] + u * (b[1] - a[1]) + v * (c[1] - a[1])
