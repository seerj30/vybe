"""Unit tests for the plain-Python geometry (no Blender): python3 -m unittest discover -s assets/source"""

from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vybe_art import body, catalog, geom, rig  # noqa: E402


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

    def test_rig_bones_have_known_parents(self) -> None:
        names = set()
        for b in rig.BONES:
            self.assertTrue(b.parent is None or b.parent in names, b.name)
            names.add(b.name)


if __name__ == "__main__":
    unittest.main()
