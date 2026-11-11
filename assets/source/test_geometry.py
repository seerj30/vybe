"""Unit tests for the plain-Python geometry (no Blender): python3 -m unittest discover -s assets/source"""

from __future__ import annotations

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vybe_art import body, catalog, geom, rig, room  # noqa: E402


def outward(mesh: geom.Mesh) -> float:
    """Signed volume around the mesh centroid: positive when the faces point outward (small openings are fine)."""
    n = float(len(mesh.verts))
    o = (sum(p[0] for p in mesh.verts) / n, sum(p[1] for p in mesh.verts) / n, sum(p[2] for p in mesh.verts) / n)
    v = 0.0
    for f in mesh.faces:
        a = geom.sub(mesh.verts[f[0]], o)
        for k in range(1, len(f) - 1):
            b, c = geom.sub(mesh.verts[f[k]], o), geom.sub(mesh.verts[f[k + 1]], o)
            v += geom.dot(a, geom.cross(b, c)) / 6
    return v


class GeometryTests(unittest.TestCase):
    def test_body_b_has_the_same_topology_for_every_asset(self) -> None:
        for asset in catalog.ASSETS:
            a, b = asset.build(rig.BODY_A), asset.build(rig.BODY_B)
            self.assertTrue(a.same_topology(b), asset.id)

    def test_weights_use_rig_bones_and_sum_to_one(self) -> None:
        meshes = body.base_meshes() + [a.build(rig.BODY_A) for a in catalog.ASSETS]
        for m in meshes:
            for w in m.weights:
                self.assertTrue(set(w) <= set(rig.BONE_NAMES), (m.name, w))
                self.assertAlmostEqual(sum(w.values()), 1.0, places=2, msg=m.name)

    def test_body_regions_face_outward(self) -> None:
        for m in body.region_meshes(rig.BODY_A):
            self.assertGreater(outward(m), 0, m.name)

    def test_mirror_keeps_faces_outward(self) -> None:
        m = geom.Mesh("box")
        geom.box(m, (0.2, 0, 1), (0.1, 0.1, 0.1), "hand_L", "x")
        mirrored = m.mirrored("box_r", rig.mirror_name)
        self.assertGreater(outward(m), 0)
        self.assertGreater(outward(mirrored), 0)
        self.assertEqual(mirrored.weights[0], {"hand_R": 1.0})

    def test_every_catalog_item_has_a_known_asset_and_unique_sku(self) -> None:
        skus = [i.sku for i in catalog.ITEMS]
        self.assertEqual(len(skus), len(set(skus)))
        for item in catalog.ITEMS:
            self.assertIn(item.asset, catalog.ASSET_BY_ID, item.sku)

    def test_body_b_reads_clearly_wider_than_body_a(self) -> None:
        def half_width(d: rig.Dims, region: str) -> float:
            m = next(m for m in body.region_meshes(d) if m.name == f"region_{region}")
            return max(p[0] for p in m.verts)

        for region, ratio in (("torso", 1.3), ("hips", 1.25), ("upper_arms", 1.08), ("upper_legs", 1.25)):
            a, b = half_width(rig.BODY_A, region), half_width(rig.BODY_B, region)
            self.assertGreater(b, a * ratio, region)

    def test_body_b_arms_turn_around_the_shoulder_joint(self) -> None:
        d = rig.BODY_B
        self.assertLess(math.dist(d.shoulder(), rig.SHOULDER), 0.02)
        self.assertAlmostEqual(math.dist(d.elbow(), d.shoulder()), math.dist(rig.ELBOW, rig.SHOULDER), places=6)
        self.assertAlmostEqual(math.dist(d.wrist(), d.elbow()), math.dist(rig.WRIST, rig.ELBOW), places=6)
        self.assertEqual(rig.BODY_A.elbow(), rig.ELBOW)
        self.assertEqual(rig.BODY_A.ankle(), rig.ANKLE)

    def test_visible_arms_stay_outside_the_torso_and_tops(self) -> None:
        """No arm skin inside the torso or inside a top that leaves the arm visible (both bodies)."""
        for d in rig.BODY_PRESETS.values():
            regions = {m.name: m for m in body.region_meshes(d)}
            # The keyword arguments of catalog._top (scale, fit, puffy) give the size of the torso part of a top.
            tops = [(a.hide, a.build.__closure__[0].cell_contents) for a in catalog.ASSETS if a.slot == "top"]
            for hide, kw in [((), {"scale": 1.0, "fit": 0.0})] + tops:
                scale = float(kw.get("scale", 1.05))
                fit = float(kw.get("fit", 0.016)) + float(kw.get("puffy", 0.0))
                for region in ("upper_arms", "lower_arms", "hands"):
                    if region in hide:
                        continue
                    for x, y, z in regions[f"region_{region}"].verts:
                        if not 0.6 < z < 0.98:
                            continue
                        rx, ry = d.torso_at(z)
                        inside = (x / (rx * scale + fit)) ** 2 + (y / (ry * scale + fit)) ** 2
                        self.assertGreater(inside, 1.0, (region, kw, (x, y, z)))

    def test_items_sit_as_close_to_body_b_as_to_body_a(self) -> None:
        """No floating items: the item-to-skin gap on body B is at most 1.5 cm more than on body A."""

        def gap(d: rig.Dims, m: geom.Mesh) -> float:
            lo = [min(p[i] for p in m.verts) - 0.05 for i in range(3)]
            hi = [max(p[i] for p in m.verts) + 0.05 for i in range(3)]
            skin = [q for r in body.region_meshes(d) for q in r.verts
                    if all(lo[i] <= q[i] <= hi[i] for i in range(3))]
            return min((math.dist(p, q) for p in m.verts for q in skin), default=1.0)

        for asset in catalog.ASSETS:
            if asset.emote or not asset.fits_bodies:
                continue
            a, b = gap(rig.BODY_A, asset.build(rig.BODY_A)), gap(rig.BODY_B, asset.build(rig.BODY_B))
            self.assertLess(b, a + 0.015, asset.id)  # body B vertices are further apart (up to 1.42x)
            self.assertLess(b, 0.05, asset.id)

    def test_rig_bones_have_known_parents(self) -> None:
        names = set()
        for b in rig.BONES:
            self.assertTrue(b.parent is None or b.parent in names, b.name)
            names.add(b.name)



class RoomGeometryTests(unittest.TestCase):
    def bounds(self, m: geom.Mesh) -> tuple:
        xs, ys, zs = zip(*m.verts)
        return min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)

    def test_room_items_stay_inside_their_footprint(self) -> None:
        for a in room.ROOM_ASSETS:
            if a.footprint is None:
                continue
            x0, x1, y0, y1, z0, z1 = self.bounds(a.build())
            w = a.footprint[0] * room.CELL / 2 + 0.02
            self.assertTrue(-w <= x0 and x1 <= w, a.id)
            if a.slot == "furniture":
                d = a.footprint[1] * room.CELL / 2 + 0.02
                self.assertTrue(-d <= y0 and y1 <= d and z0 >= -0.02 and z1 <= 2.4, a.id)
            else:
                self.assertTrue(-0.3 <= y0 and y1 <= 0.02 and z0 >= -0.02 and z1 <= a.footprint[1] * room.CELL + 0.02, a.id)

    def test_every_room_item_has_a_known_asset_and_a_unique_sku(self) -> None:
        skus = [i.sku for i in room.ROOM_ITEMS] + [i.sku for i in catalog.ITEMS]
        self.assertEqual(len(skus), len(set(skus)))
        for item in room.ROOM_ITEMS:
            self.assertIn(item.asset, room.ROOM_ASSET_BY_ID, item.sku)

    def test_rotation_swaps_the_footprint_and_keeps_the_center_on_its_cells(self) -> None:
        self.assertEqual(room.rotated((4, 2), 90), (2, 4))
        self.assertEqual(room.rotated((4, 2), 180), (4, 2))
        (x, y, _z), r = room.place_floor(0, 0, (4, 2), 90)
        self.assertAlmostEqual(x, -2 + 0.5)  # 2 cells wide after the turn
        self.assertAlmostEqual(y, 2 - 1.0)  # 4 cells deep, from the back wall
        self.assertAlmostEqual(r, math.pi / 2)

    def test_left_wall_items_face_into_the_room(self) -> None:
        (x, y, z), r = room.place_wall("wall_left", 0, 2, (2, 2))
        self.assertEqual((x, z), (-room.HALF, 1.0))
        self.assertAlmostEqual(y, room.HALF - 0.5)  # column 0 starts at the back
        self.assertAlmostEqual(r, math.pi / 2)


if __name__ == "__main__":
    unittest.main()
