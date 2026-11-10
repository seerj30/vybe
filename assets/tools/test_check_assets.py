"""Unit tests for the asset checker and the glb reader (python3 -m unittest discover -s assets/tools)."""

from __future__ import annotations

import json
import os
import shutil
import struct
import sys
import tempfile
import zlib
import unittest
from typing import Any, Callable, Dict, List

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import check_assets  # noqa: E402
import compare_build  # noqa: E402
import glb  # noqa: E402
import publish  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))


class CheckerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.mkdtemp(prefix="vybe-assets-")
        os.makedirs(os.path.join(self.tmp, "assets"))
        shutil.copytree(os.path.join(REPO, "assets", "manifests"), os.path.join(self.tmp, "assets", "manifests"))
        os.symlink(os.path.join(REPO, "assets", "published"), os.path.join(self.tmp, "assets", "published"))

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp)

    def edit(self, name: str, change: Callable[[Dict[str, Any]], None]) -> None:
        path = os.path.join(self.tmp, "assets", "manifests", name)
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        change(doc)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f)

    def item(self, doc: Dict[str, Any], sku: str) -> Dict[str, Any]:
        return next(i for i in doc["items"] if i["sku"] == sku)

    def errors(self) -> List[str]:
        return check_assets.Checker(self.tmp).run()

    def assert_error(self, fragment: str) -> None:
        errors = self.errors()
        self.assertTrue(any(fragment in e for e in errors), f"no error with {fragment!r} in {errors}")

    def test_repository_assets_pass(self) -> None:
        self.assertEqual(check_assets.Checker(REPO).run(), [])

    def test_over_budget_triangles_fail(self) -> None:
        self.edit("avatar-3d.v1.json", lambda d: d["budgets"].update(avatarTriangles=1000))
        self.assert_error("worst-case visible avatar has")

    def test_over_budget_download_fails(self) -> None:
        self.edit("avatar-3d.v1.json", lambda d: d["budgets"].update(initialDownloadBytes=1000))
        self.assert_error("initial avatar download")

    def test_wrong_rig_version_fails(self) -> None:
        self.edit("catalog.v1.json", lambda d: self.item(d, "top_court_jersey").update(rigVersion="vybe-rig-0"))
        self.assert_error("top_court_jersey: rigVersion vybe-rig-0")

    def test_missing_field_fails(self) -> None:
        self.edit("catalog.v1.json", lambda d: self.item(d, "acc_chain").pop("contentHash"))
        self.assert_error("acc_chain: missing or wrong-type fields: contentHash")

    def test_wrong_size_or_hash_fails(self) -> None:
        def change(d: Dict[str, Any]) -> None:
            self.item(d, "hair_mohawk")["downloadBytes"] += 1
            self.item(d, "hair_spikes")["contentHash"] = "sha256:" + "0" * 64

        self.edit("catalog.v1.json", change)
        self.assert_error("hair_mohawk: downloadBytes")
        self.assert_error("hair_spikes: contentHash")

    def test_mask_gap_fails(self) -> None:
        # Shorts end at the knee: hiding the lower legs would show a hole.
        self.edit("catalog.v1.json",
                  lambda d: self.item(d, "bottom_starter_shorts")["hideBodyRegions"].append("lower_legs"))
        self.assert_error("bottom_starter_shorts: hides lower_legs")

    def test_required_mask_fails(self) -> None:
        self.edit("catalog.v1.json", lambda d: self.item(d, "top_long_sleeve")["hideBodyRegions"].remove("torso"))
        self.assert_error("a top must hide the torso region")

    def test_price_outside_band_fails(self) -> None:
        self.edit("catalog.v1.json", lambda d: self.item(d, "acc_court_crown").update(price=500))
        self.assert_error("epic price 500 is outside 6000–10000")

    def test_too_few_priced_items_fail(self) -> None:
        self.edit("catalog.v1.json", lambda d: d.update(items=[i for i in d["items"] if i["price"] < 600]))
        self.assert_error("the beta needs at least 30")

    def test_unknown_body_and_attach_bone_fail(self) -> None:
        def change(d: Dict[str, Any]) -> None:
            jersey = self.item(d, "top_court_jersey")
            jersey["bodyCompatibility"] = jersey["compatibleBodyPresets"] = ["body_c"]
            self.item(d, "acc_shades")["attachBone"] = "tail"

        self.edit("catalog.v1.json", change)
        self.assert_error("unknown or no body presets")
        self.assert_error("attachBone 'tail'")

    def test_mutable_url_fails(self) -> None:
        self.edit("catalog.v1.json", lambda d: self.item(d, "shoes_runners").update(assetUrl="/assets/3d/items/runners.glb"))
        self.assert_error("is not an immutable")


class GlbTests(unittest.TestCase):
    def test_rejects_non_glb(self) -> None:
        with self.assertRaises(glb.GlbError):
            glb.read(b"not a glb file at all")

    def test_rejects_wrong_version(self) -> None:
        data = struct.pack("<III", glb.GLB_MAGIC, 1, 20) + struct.pack("<II", 0, glb.CHUNK_JSON)
        with self.assertRaises(glb.GlbError):
            glb.read(data)

    def test_reads_png_and_jpeg_sizes(self) -> None:
        png = b"\x89PNG\r\n\x1a\n" + struct.pack(">I4sII", 13, b"IHDR", 512, 256) + b"\x08\x06\x00\x00\x00"
        self.assertEqual(glb.image_size("image/png", png), (512, 256))
        jpeg = b"\xff\xd8" + b"\xff\xc0" + struct.pack(">HBHH", 17, 8, 300, 640) + b"\x00" * 12
        self.assertEqual(glb.image_size("image/jpeg", jpeg), (640, 300))


class PublishTests(unittest.TestCase):
    def test_strips_png_text_chunks(self) -> None:
        def chunk(kind: bytes, body: bytes) -> bytes:
            return struct.pack(">I", len(body)) + kind + body + b"\x00\x00\x00\x00"

        sig = b"\x89PNG\r\n\x1a\n"
        ihdr = chunk(b"IHDR", b"\x00" * 13)
        end = chunk(b"IEND", b"")
        stamped = sig + ihdr + chunk(b"tEXt", b"Date\x002026/11/10") + chunk(b"tIME", b"\x00" * 7) + end
        self.assertEqual(publish.strip_png_metadata(stamped), sig + ihdr + end)


def encode_png(width: int, height: int, channels: int, pixels: bytes) -> bytes:
    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    stride = width * channels
    raw = b"".join(b"\x00" + pixels[y * stride:(y + 1) * stride] for y in range(height))
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6 if channels == 4 else 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


class CompareTests(unittest.TestCase):
    def thumb(self) -> bytes:
        folder = os.path.join(REPO, "assets", "published", "3d", "thumbs")
        with open(os.path.join(folder, sorted(os.listdir(folder))[0]), "rb") as f:
            return f.read()

    def test_decodes_a_pipeline_thumbnail(self) -> None:
        w, h, c, px = compare_build.decode_png(self.thumb())
        self.assertEqual((w, h, c, len(px)), (256, 256, 4, 256 * 256 * 4))
        self.assertEqual(compare_build.changed_share(self.thumb(), encode_png(w, h, c, px)), 0.0)

    def test_accepts_renderer_noise_and_rejects_a_changed_picture(self) -> None:
        w, h, c, px = compare_build.decode_png(self.thumb())
        noisy = bytearray(px)
        for p in range(0, 200 * c, c):  # 200 pixels: 0.3 %
            noisy[p] = (noisy[p] + 100) % 256
        self.assertLessEqual(compare_build.changed_share(self.thumb(), encode_png(w, h, c, bytes(noisy))), compare_build.MAX_CHANGED)
        inverted = bytes(255 - v for v in px)
        self.assertGreater(compare_build.changed_share(self.thumb(), encode_png(w, h, c, inverted)), compare_build.MAX_CHANGED)


if __name__ == "__main__":
    unittest.main()
