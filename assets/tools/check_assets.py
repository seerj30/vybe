"""Asset budget and compatibility check (spec §9, §13 "asset manifest validation"). CI runs it on every PR.

    python3 assets/tools/check_assets.py            check the repository; exit 1 on any error

It reads only the committed files (manifests, assets/published), so it needs no Blender. Checks:
manifest fields and types, price bands (decision 5), file hashes and sizes, triangle and texture counts,
the rig (bone names and bind pose equal to the base avatar), body compatibility (shape key), attachment
bones, coverage masks (a hidden body region must be covered by the item), emotes, thumbnails and the
avatar budgets (triangles, initial download, draw calls, texture size). My Room (room.v1.json): grid, shell,
room item fields, slots and surfaces, footprints (the mesh stays inside its cells), static meshes (no skin),
per-item budgets and the worst-case room scene (docs/design/p1-my-room.md).
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import struct
import sys
from typing import Any, Dict, List, Optional, Sequence, Set, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import glb  # noqa: E402

SLOTS = ("hair", "top", "bottom", "shoes", "accessory", "emote")
BANDS = {"common": (300, 800), "rare": (1500, 3000), "epic": (6000, 10000)}
MIN_PRICED_ITEMS = 30
ITEM_FIELDS = {
    "sku": str, "slot": str, "name": str, "price": int, "minLevel": int, "assetKey": str,
    "compatibleBodyPresets": list, "rarity": str, "assetVersion": int, "assetUrl": str, "thumbnailUrl": str,
    "rigVersion": str, "bodyCompatibility": list, "hideBodyRegions": list, "textureVariants": list,
    "triangles": int, "textureBytes": int, "downloadBytes": int, "contentHash": str,
}
URL_RE = re.compile(r"^/assets/3d/(avatar|items|thumbs|room)/([a-z0-9_]+)\.([0-9a-f]{12})\.(glb|png)$")
HEX_RE = re.compile(r"^#[0-9a-f]{6}$")
HIDE_HAIR = "hair"
COVER_TOLERANCE = 0.012  # meters
REST_TOLERANCE = 1e-4
THUMB_PX = 256
MAX_EMOTE_SECONDS = 6.0


ROOM_SLOTS = {"furniture": ["floor"], "wall_decor": ["wall_back", "wall_left"], "room_wall": [], "room_floor": []}
ROOM_FIELDS = {
    "sku": str, "slot": str, "name": str, "price": int, "minLevel": int, "rarity": str, "assetKey": str,
    "assetVersion": int, "assetUrl": str, "thumbnailUrl": str, "surfaces": list, "textureVariants": list,
    "triangles": int, "drawCalls": int, "textureBytes": int, "downloadBytes": int, "contentHash": str,
}
FINISH_NODES = {"room_wall": "walls", "room_floor": "floor"}
MAX_FOOTPRINT = {"furniture": (4, 4), "wall_decor": (4, 3)}


class Checker:
    def __init__(self, repo: str) -> None:
        self.repo = repo
        self.errors: List[str] = []
        self._glb: Dict[str, glb.Glb] = {}

    def error(self, where: str, msg: str) -> None:
        self.errors.append(f"{where}: {msg}")

    # ------------------------------------------------------------ files

    def path(self, url: str) -> str:
        return os.path.join(self.repo, "assets", "published", url[len("/assets/"):])

    def load(self, where: str, url: str, content_hash: Optional[str] = None, size: Optional[int] = None) -> Optional[bytes]:
        m = URL_RE.match(url)
        if not m:
            self.error(where, f"URL {url!r} is not an immutable /assets/3d/<folder>/<name>.<hash12>.<ext> path")
            return None
        p = self.path(url)
        if not os.path.isfile(p):
            self.error(where, f"file {url} does not exist")
            return None
        with open(p, "rb") as f:
            data = f.read()
        h = hashlib.sha256(data).hexdigest()
        if h[:12] != m.group(3):
            self.error(where, f"{url}: the name hash does not match the content")
        if content_hash is not None and content_hash != f"sha256:{h}":
            self.error(where, f"contentHash {content_hash} does not match the file (sha256:{h})")
        if size is not None and size != len(data):
            self.error(where, f"downloadBytes {size} does not match the file size {len(data)}")
        return data

    def parse(self, where: str, url: str, data: bytes) -> Optional[glb.Glb]:
        try:
            g = glb.read(data)
        except (glb.GlbError, ValueError, KeyError) as e:
            self.error(where, f"{url}: invalid glTF binary: {e}")
            return None
        self._glb[url] = g
        return g

    # ------------------------------------------------------------ run

    def run(self) -> List[str]:
        manifests = os.path.join(self.repo, "assets", "manifests")
        avatar = _read_json(os.path.join(manifests, "avatar-3d.v1.json"))
        presets = _read_json(os.path.join(manifests, "avatar.v1.json"))
        catalog = _read_json(os.path.join(manifests, "catalog.v1.json"))
        base = self.check_avatar(avatar, presets)
        if base is None:
            return self.errors
        self.check_catalog(catalog, avatar, base)
        self.check_room(_read_json(os.path.join(manifests, "room.v1.json")), {i.get("sku") for i in catalog.get("items", [])})
        return self.errors

    # ------------------------------------------------------------ My Room

    def check_room(self, room: Dict[str, Any], avatar_skus: Set[str]) -> None:
        w = "room.v1.json"
        for key in ("version", "cellMeters", "floor", "wall", "maxItems", "surfaces", "rotations", "shell", "items",
                    "budgets"):
            if key not in room:
                self.error(w, f"missing field {key}")
        if self.errors:
            return
        b = room["budgets"]
        cell = room["cellMeters"]
        if room["surfaces"] != ["floor", "wall_back", "wall_left"] or room["rotations"] != [0, 90, 180, 270]:
            self.error(w, "surfaces or rotations differ from the design (docs/design/p1-my-room.md)")
        shell = room["shell"]
        data = self.load(w, shell["assetUrl"], shell.get("contentHash"), shell.get("downloadBytes"))
        shell_glb = self.parse(w, shell["assetUrl"], data) if data is not None else None
        if shell_glb is not None:
            names = {n.get("name") for n in shell_glb.mesh_nodes()}
            for node in shell["nodes"].values():
                if node not in names:
                    self.error(w, f"shell has no mesh node {node}")
            self.check_static(w, shell_glb)
            if shell["downloadBytes"] > b["shellDownloadBytes"]:
                self.error(w, f"shell download {shell['downloadBytes']} bytes; the budget is {b['shellDownloadBytes']}")
        skus: Set[str] = set()
        placeable: List[Tuple[int, int, int]] = []  # (triangles, draw calls, bytes)
        finishes: Dict[str, List[Tuple[int, int, int]]] = {"room_wall": [], "room_floor": []}
        for item in room["items"]:
            sku = item.get("sku", "?")
            where = f"{w} {sku}"
            missing = [k for k, t in ROOM_FIELDS.items()
                       if not isinstance(item.get(k), t) or (t is int and isinstance(item.get(k), bool))]
            if missing:
                self.error(where, f"missing or wrong-type fields: {', '.join(missing)}")
                continue
            if sku in skus or sku in avatar_skus:
                self.error(where, "duplicate sku (room and avatar skus share one catalog)")
            skus.add(sku)
            slot = item["slot"]
            if slot not in ROOM_SLOTS:
                self.error(where, f"unknown room slot {slot}")
                continue
            if item["surfaces"] != ROOM_SLOTS[slot]:
                self.error(where, f"surfaces {item['surfaces']} must be {ROOM_SLOTS[slot]} for slot {slot}")
            if item["rarity"] not in BANDS:
                self.error(where, f"unknown rarity {item['rarity']}")
            else:
                lo, hi = BANDS[item["rarity"]]
                if not lo <= item["price"] <= hi:
                    self.error(where, f"{item['rarity']} price {item['price']} is outside {lo}–{hi} (decision 5)")
            for v in item["textureVariants"]:
                if not (isinstance(v, dict) and isinstance(v.get("material"), str) and HEX_RE.match(str(v.get("color")))):
                    self.error(where, f"textureVariants entry {v!r} must be {{material, color: #rrggbb}}")
            fp = item.get("footprint")
            if slot in MAX_FOOTPRINT:
                second = "d" if slot == "furniture" else "h"
                mw, md = MAX_FOOTPRINT[slot]
                if not (isinstance(fp, dict) and set(fp) == {"w", second} and all(isinstance(fp[k], int) for k in fp)
                        and 1 <= fp["w"] <= mw and 1 <= fp[second] <= md):
                    self.error(where, f"footprint {fp!r} must be {{w: 1–{mw}, {second}: 1–{md}}} cells")
                    fp = None
                if slot == "furniture" and item.get("layer") not in ("rug", "object"):
                    self.error(where, "a furniture item needs layer rug or object")
            else:
                if fp is not None or item.get("layer") is not None:
                    self.error(where, "a finish has no footprint and no layer")
                if item.get("replaces") != FINISH_NODES[slot]:
                    self.error(where, f"a {slot} finish must replace the shell node {FINISH_NODES[slot]}")
            data = self.load(where, item["assetUrl"], item["contentHash"], item["downloadBytes"])
            self.check_thumbnail(where, item["thumbnailUrl"])
            if data is None:
                continue
            g = self._glb.get(item["assetUrl"]) or self.parse(where, item["assetUrl"], data)
            if g is None:
                continue
            tris, calls = g.triangles(), g.draw_calls()
            if tris != item["triangles"] or calls != item["drawCalls"] or g.texture_bytes() != item["textureBytes"]:
                self.error(where, "triangles, drawCalls or textureBytes do not match the file")
            if g.texture_bytes() > 0:
                self.error(where, "room items have no textures (flat colors)")
            self.check_static(where, g)
            mats = {m.get("name") for m in g.json.get("materials", [])}
            for v in item["textureVariants"]:
                if isinstance(v, dict) and v.get("material") not in mats:
                    self.error(where, f"textureVariants material {v.get('material')!r} is not in the file")
            if slot in FINISH_NODES:
                if tris > b["finishTriangles"]:
                    self.error(where, f"{tris} triangles; a finish may have {b['finishTriangles']}")
                finishes[slot].append((tris, calls, item["downloadBytes"]))
                continue
            if tris > b["itemTriangles"]:
                self.error(where, f"{tris} triangles; a room item may have {b['itemTriangles']}")
            if calls > b["itemDrawCalls"]:
                self.error(where, f"{calls} draw calls; a room item may have {b['itemDrawCalls']}")
            if item["downloadBytes"] > b["itemDownloadBytes"]:
                self.error(where, f"{item['downloadBytes']} bytes; a room item may have {b['itemDownloadBytes']}")
            placeable.append((tris, calls, item["downloadBytes"]))
            if fp is not None:
                self.check_footprint(where, g, slot, fp, cell, b)
        self.check_room_budgets(room, placeable, finishes)

    def check_static(self, where: str, g: glb.Glb) -> None:
        if g.json.get("skins") or g.json.get("animations"):
            self.error(where, "room files are static: no skin and no animation")
        if not g.mesh_nodes():
            self.error(where, "the file has no mesh")

    def check_footprint(self, where: str, g: glb.Glb, slot: str, fp: Dict[str, int], cell: float,
                        b: Dict[str, Any]) -> None:
        """The mesh must stay inside its cells (glTF: Y up, front +Z; floor origin at the footprint center,
        wall origin at the bottom center of the back face)."""
        lo, hi = _bounds(g, g.mesh_nodes())
        tol = b["footprintToleranceMeters"]
        half_w = fp["w"] * cell / 2
        if lo[0] < -half_w - tol or hi[0] > half_w + tol:
            self.error(where, f"mesh x {lo[0]:.3f}–{hi[0]:.3f} m is wider than {fp['w']} cells")
        if slot == "furniture":
            half_d = fp["d"] * cell / 2
            if lo[2] < -half_d - tol or hi[2] > half_d + tol:
                self.error(where, f"mesh z {lo[2]:.3f}–{hi[2]:.3f} m is deeper than {fp['d']} cells")
            if lo[1] < -tol or hi[1] > b["floorItemMaxHeightMeters"]:
                self.error(where, f"mesh y {lo[1]:.3f}–{hi[1]:.3f} m: it must stand on the floor and be at most "
                                  f"{b['floorItemMaxHeightMeters']} m high")
        else:
            if lo[1] < -tol or hi[1] > fp["h"] * cell + tol:
                self.error(where, f"mesh y {lo[1]:.3f}–{hi[1]:.3f} m is higher than {fp['h']} cells")
            if lo[2] < -tol or hi[2] > b["wallItemMaxDepthMeters"]:
                self.error(where, f"mesh z {lo[2]:.3f}–{hi[2]:.3f} m: a wall item is 0–{b['wallItemMaxDepthMeters']} m deep")

    def check_room_budgets(self, room: Dict[str, Any], placeable: List[Tuple[int, int, int]],
                           finishes: Dict[str, List[Tuple[int, int, int]]]) -> None:
        """Worst case: the shell, the heaviest wall and floor finishes and the maxItems heaviest placeable items."""
        b = room["budgets"]
        shell = room["shell"]
        top = sorted(placeable, reverse=True)[:room["maxItems"]]
        fin = [max(f) for f in finishes.values() if f]
        tris = shell["triangles"] + sum(t for t, _c, _b in top) + sum(t for t, _c, _b in fin)
        calls = shell["drawCalls"] + sum(c for _t, c, _b in top) + sum(c for _t, c, _b in fin)
        heavy = shell["downloadBytes"] + sum(x for _t, _c, x in sorted(placeable, key=lambda p: -p[2])[:room["maxItems"]])
        heavy += sum(max(x for _t, _c, x in f) for f in finishes.values() if f)
        if tris > b["roomTriangles"]:
            self.error("room budgets", f"worst-case room has {tris} triangles; the budget is {b['roomTriangles']}")
        if calls > b["roomDrawCalls"]:
            self.error("room budgets", f"worst-case room needs {calls} draw calls; the budget is {b['roomDrawCalls']}")
        if heavy > b["roomDownloadBytes"]:
            self.error("room budgets", f"worst-case room download is {heavy} bytes; the budget is {b['roomDownloadBytes']}")
        self.room_summary = {"roomTriangles": tris, "roomDrawCalls": calls, "roomBytes": heavy,
                             "shellBytes": shell["downloadBytes"], "roomItems": len(room["items"])}

    # ------------------------------------------------------------ base avatar

    def check_avatar(self, avatar: Dict[str, Any], presets: Dict[str, Any]) -> Optional[glb.Glb]:
        w = "avatar-3d.v1.json"
        for key in ("rigVersion", "bones", "regions", "attachBones", "requiredMasks", "base", "bodyMorph",
                    "bodyPresets", "skinTones", "hairColors", "faces", "hairs", "idleAnimation", "starterItems",
                    "budgets"):
            if key not in avatar:
                self.error(w, f"missing field {key}")
        if self.errors:
            return None
        for key, ids in (("skinTones", presets["skinTones"]), ("hairColors", presets["hairColors"]),
                         ("faces", presets["faces"]), ("hairs", presets["hairs"]),
                         ("bodyPresets", presets["bodyPresets"])):
            have = list(avatar[key]) if isinstance(avatar[key], dict) else avatar[key]
            if sorted(have) != sorted(ids):
                self.error(w, f"{key} {sorted(have)} differ from avatar.v1.json {sorted(ids)}")
        for key in ("skinTones", "hairColors"):
            for pid, color in avatar[key].items():
                if not HEX_RE.match(color):
                    self.error(w, f"{key}.{pid}: {color!r} is not a #rrggbb color")
        b = avatar["base"]
        data = self.load(w, b["assetUrl"], b.get("contentHash"), b.get("downloadBytes"))
        if data is None:
            return None
        g = self.parse(w, b["assetUrl"], data)
        if g is None:
            return None
        if g.triangles() != b.get("triangles"):
            self.error(w, f"base triangles {b.get('triangles')} != {g.triangles()} in the file")
        joints = g.skin_joint_names()
        if sorted(joints) != sorted(avatar["bones"]):
            self.error(w, f"base skin joints {sorted(joints)} differ from the rig bones")
        names = g.node_names()
        for region in avatar["regions"]:
            node = _node(g, f"region_{region}")
            if node is None:
                self.error(w, f"base has no mesh node region_{region}")
            elif avatar["bodyMorph"] not in g.morph_targets(node):
                self.error(w, f"region_{region} has no {avatar['bodyMorph']} shape key")
        for n in list(avatar["faces"]) + list(avatar["hairs"]):
            if n not in names:
                self.error(w, f"base has no node {n}")
        anims = [a.get("name") for a in g.json.get("animations", [])]
        if avatar["idleAnimation"] not in anims:
            self.error(w, f"base has no animation {avatar['idleAnimation']!r}")
        # Y-up, meters, origin at ground center (spec §9).
        lo, hi = _bounds(g, [_node(g, f"region_{r}") for r in avatar["regions"]])
        if abs(lo[1]) > 0.01:
            self.error(w, f"base lowest point y={lo[1]:.3f}; the origin must be at the ground (y=0)")
        if not 1.3 <= hi[1] <= 1.8:
            self.error(w, f"base height {hi[1]:.3f} m is outside 1.3–1.8 m (meters, Y up)")
        if abs((lo[0] + hi[0]) / 2) > 0.02 or abs((lo[2] + hi[2]) / 2) > 0.06:
            self.error(w, "base is not centered on the origin")
        return g

    # ------------------------------------------------------------ catalog

    def check_catalog(self, catalog: Dict[str, Any], avatar: Dict[str, Any], base: glb.Glb) -> None:
        w = "catalog.v1.json"
        if not isinstance(catalog.get("catalogVersion"), int) or catalog["catalogVersion"] < 1:
            self.error(w, "catalogVersion must be a positive integer")
        items = catalog.get("items", [])
        skus: Set[str] = set()
        priced = 0
        base_rest = base.bone_rest()
        regions: Dict[str, List[str]] = avatar["regions"]
        region_extent = {r: _region_extent(base, _node(base, f"region_{r}")) for r in regions}
        worst: Dict[str, Tuple[int, int, int]] = {}  # slot -> (triangles, draw calls, bytes), the largest item
        acc: List[Tuple[int, int, int]] = []
        starters = avatar["starterItems"]
        by_sku = {i.get("sku"): i for i in items}
        for slot, sku in starters.items():
            if sku not in by_sku or not by_sku[sku].get("starter") or by_sku[sku].get("slot") != slot:
                self.error(w, f"starter item for {slot} ({sku}) is missing or not a starter of that slot")

        for item in items:
            sku = item.get("sku", "?")
            where = f"{w} {sku}"
            missing = [k for k, t in ITEM_FIELDS.items()
                       if not isinstance(item.get(k), t) or (t is int and isinstance(item.get(k), bool))]
            if missing:
                self.error(where, f"missing or wrong-type fields: {', '.join(missing)}")
                continue
            if sku in skus:
                self.error(where, "duplicate sku")
            skus.add(sku)
            slot = item["slot"]
            if slot not in SLOTS:
                self.error(where, f"unknown slot {slot}")
                continue
            if item["rarity"] not in BANDS:
                self.error(where, f"unknown rarity {item['rarity']}")
            elif item.get("starter"):
                if item["price"] != 0:
                    self.error(where, "a starter item must cost 0")
            else:
                lo, hi = BANDS[item["rarity"]]
                if not lo <= item["price"] <= hi:
                    self.error(where, f"{item['rarity']} price {item['price']} is outside {lo}–{hi} (decision 5)")
                priced += 1
            if item["rigVersion"] != avatar["rigVersion"]:
                self.error(where, f"rigVersion {item['rigVersion']} != {avatar['rigVersion']}")
            if item["bodyCompatibility"] != item["compatibleBodyPresets"]:
                self.error(where, "bodyCompatibility differs from compatibleBodyPresets")
            bad_bodies = [b for b in item["bodyCompatibility"] if b not in avatar["bodyPresets"]] or (
                [] if item["bodyCompatibility"] else ["(none)"])
            if bad_bodies:
                self.error(where, f"unknown or no body presets: {bad_bodies}")
            attach = item.get("attachBone")
            if slot == "accessory":
                if attach not in avatar["attachBones"]:
                    self.error(where, f"attachBone {attach!r} is not one of {avatar['attachBones']}")
            elif attach is not None:
                self.error(where, "only accessories have an attachBone")
            hide = item["hideBodyRegions"]
            unknown = [r for r in hide if r not in regions and r != HIDE_HAIR]
            if unknown:
                self.error(where, f"unknown hideBodyRegions {unknown}")
            for r in avatar["requiredMasks"].get(slot, []):
                if r not in hide:
                    self.error(where, f"a {slot} must hide the {r} region (coverage mask)")
            for v in item["textureVariants"]:
                if not (isinstance(v, dict) and isinstance(v.get("material"), str) and HEX_RE.match(str(v.get("color")))):
                    self.error(where, f"textureVariants entry {v!r} must be {{material, color: #rrggbb}}")

            data = self.load(where, item["assetUrl"], item["contentHash"], item["downloadBytes"])
            self.check_thumbnail(where, item["thumbnailUrl"])
            if data is None:
                continue
            g = self._glb.get(item["assetUrl"]) or self.parse(where, item["assetUrl"], data)
            if g is None:
                continue
            if g.triangles() != item["triangles"]:
                self.error(where, f"triangles {item['triangles']} != {g.triangles()} in the file")
            if g.texture_bytes() != item["textureBytes"]:
                self.error(where, f"textureBytes {item['textureBytes']} != {g.texture_bytes()} in the file")
            for mime, img in g.images():
                wpx, hpx = glb.image_size(mime, img)
                if max(wpx, hpx) > avatar["budgets"]["textureMaxPx"]:
                    self.error(where, f"texture {wpx}x{hpx} is larger than {avatar['budgets']['textureMaxPx']} px")
            mats = {m.get("name") for m in g.json.get("materials", [])}
            for v in item["textureVariants"]:
                if isinstance(v, dict) and v.get("material") not in mats:
                    self.error(where, f"textureVariants material {v.get('material')!r} is not in the file")
            for joint, rest in g.bone_rest().items():
                ref = base_rest.get(joint)
                if ref is None:
                    self.error(where, f"joint {joint} is not a rig bone")
                elif not _close(rest[0], ref[0]) or not _close(rest[1], ref[1]) or rest[2] != ref[2]:
                    self.error(where, f"joint {joint} bind pose differs from the rig ({avatar['rigVersion']})")
            if slot == "emote":
                self.check_emote(where, g, avatar["bones"])
                continue
            nodes = g.mesh_nodes()
            if not nodes:
                self.error(where, "the file has no mesh")
                continue
            if "body_b" in item["bodyCompatibility"] and slot not in ("hair",) and attach != "head":
                for n in nodes:
                    if avatar["bodyMorph"] not in g.morph_targets(n):
                        self.error(where, f"mesh {n.get('name')} has no {avatar['bodyMorph']} shape key for body_b")
            visible = nodes
            if slot == "accessory":
                visible = self.check_accessory(where, g, attach)
            tris, calls = g.triangles(visible), g.draw_calls(visible)
            if slot in ("top", "bottom", "shoes"):
                self.check_cover(where, g, nodes, hide, regions, region_extent)
            if slot == "accessory":
                acc.append((tris, calls, item["downloadBytes"]))
            elif tris > worst.get(slot, (0, 0, 0))[0] or item["downloadBytes"] > worst.get(slot, (0, 0, 0))[2]:
                prev = worst.get(slot, (0, 0, 0))
                worst[slot] = (max(tris, prev[0]), max(calls, prev[1]), max(item["downloadBytes"], prev[2]))

        if priced < MIN_PRICED_ITEMS:
            self.error(w, f"{priced} priced items; the beta needs at least {MIN_PRICED_ITEMS} (spec §8)")
        self.check_budgets(avatar, base, worst, acc, [by_sku[s] for s in starters.values() if s in by_sku])

    def check_thumbnail(self, where: str, url: str) -> None:
        data = self.load(where, url)
        if data is None:
            return
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            self.error(where, f"{url} is not a PNG")
            return
        wpx, hpx = struct.unpack(">II", data[16:24])
        if (wpx, hpx) != (THUMB_PX, THUMB_PX):
            self.error(where, f"thumbnail is {wpx}x{hpx}, need {THUMB_PX}x{THUMB_PX}")

    def check_emote(self, where: str, g: glb.Glb, bones: Sequence[str]) -> None:
        if g.mesh_nodes():
            self.error(where, "an emote file must not contain meshes")
        anims = g.json.get("animations", [])
        if len(anims) != 1:
            self.error(where, f"an emote file needs exactly 1 animation, found {len(anims)}")
            return
        names = g.node_names()
        for ch in anims[0]["channels"]:
            target = names[ch["target"]["node"]]
            if target not in bones:
                self.error(where, f"animation targets {target!r}, which is not a rig bone")
        end = max(max(t[0] for t in g.accessor(s["input"])) for s in anims[0]["samplers"])
        if end > MAX_EMOTE_SECONDS:
            self.error(where, f"emote is {end:.1f} s long; the limit is {MAX_EMOTE_SECONDS} s")

    def check_accessory(self, where: str, g: glb.Glb, attach: Optional[str]) -> List[Dict[str, Any]]:
        nodes = {n.get("name", ""): n for n in g.mesh_nodes()}
        p1 = [n for name, n in nodes.items() if name.endswith("_point_1")]
        p2 = [n for name, n in nodes.items() if name.endswith("_point_2")]
        if len(p1) != 1:
            self.error(where, "an accessory needs exactly one mesh named <asset>_point_1")
            return list(nodes.values())
        if attach and attach.endswith("_L") and len(p2) != 1:
            self.error(where, f"{attach} is a left bone: the file needs a mirrored <asset>_point_2 mesh")
        names = g.node_names()
        skin = g.json["skins"][p1[0]["skin"]] if "skin" in p1[0] else None
        if skin is None:
            self.error(where, "the accessory mesh is not skinned to the rig")
        else:
            for _pos, joints, weights in g.positions(p1[0]):
                main = names[skin["joints"][int(joints[max(range(4), key=lambda k: weights[k])])]]
                if main != attach:
                    self.error(where, f"accessory vertices follow {main}, not attachBone {attach}")
                    break
        return p1

    def check_cover(self, where: str, g: glb.Glb, nodes: Sequence[Dict[str, Any]], hide: Sequence[str],
                    regions: Dict[str, List[str]], extent: Dict[str, Optional[Tuple[float, float]]]) -> None:
        """A hidden region must be covered: the item reaches as low and as high as the body region."""
        names = g.node_names()
        for region in hide:
            if region == HIDE_HAIR or extent.get(region) is None:
                continue
            bones = set(regions[region])
            ys: List[float] = []
            for n in nodes:
                skin = g.json["skins"][n["skin"]]
                for pos, joints, weights in g.positions(n):
                    share = sum(weights[k] for k in range(4) if names[skin["joints"][int(joints[k])]] in bones)
                    if share >= 0.5:
                        ys.append(pos[1])
            lo, hi = extent[region]  # type: ignore[misc]
            if not ys:
                self.error(where, f"hides {region} but has no part on that region (clipping mask gap)")
                continue
            if min(ys) > lo + COVER_TOLERANCE or max(ys) < hi - COVER_TOLERANCE:
                self.error(where, f"hides {region} ({lo:.3f}–{hi:.3f} m) but covers only {min(ys):.3f}–{max(ys):.3f} m "
                                  "(clipping mask gap)")

    def check_budgets(self, avatar: Dict[str, Any], base: glb.Glb, worst: Dict[str, Tuple[int, int, int]],
                      acc: List[Tuple[int, int, int]], starters: List[Dict[str, Any]]) -> None:
        w = "budgets"
        b = avatar["budgets"]
        regions = [_node(base, f"region_{r}") for r in avatar["regions"]]
        faces = [_node(base, f) for f in avatar["faces"]]
        hairs = [_node(base, h) for h in avatar["hairs"]]
        body_tris = base.triangles(regions) + max(base.triangles([f]) for f in faces)
        body_calls = base.draw_calls(regions) + max(base.draw_calls([f]) for f in faces)
        preset_hair = max((base.triangles([h]), base.draw_calls([h])) for h in hairs)
        hair = max(preset_hair, worst.get("hair", (0, 0, 0))[:2])
        top2 = sorted(acc, reverse=True)[:2]
        tris = body_tris + hair[0] + sum(worst.get(s, (0, 0, 0))[0] for s in ("top", "bottom", "shoes")) + sum(a[0] for a in top2)
        calls = body_calls + hair[1] + sum(worst.get(s, (0, 0, 0))[1] for s in ("top", "bottom", "shoes")) + sum(a[1] for a in top2)
        if tris > b["avatarTriangles"]:
            self.error(w, f"worst-case visible avatar has {tris} triangles; the budget is {b['avatarTriangles']}")
        if calls > b["drawCalls"]:
            self.error(w, f"worst-case avatar needs {calls} draw calls; the budget is {b['drawCalls']}")
        initial = avatar["base"]["downloadBytes"] + sum(s["downloadBytes"] for s in starters)
        if initial > b["initialDownloadBytes"]:
            self.error(w, f"initial avatar download is {initial} bytes; the budget is {b['initialDownloadBytes']}")
        heavy = avatar["base"]["downloadBytes"] + sum(worst.get(s, (0, 0, 0))[2] for s in ("hair", "top", "bottom", "shoes", "emote")) + sum(
            sorted((a[2] for a in acc), reverse=True)[:2])
        if heavy > b["initialDownloadBytes"]:
            self.error(w, f"heaviest outfit download is {heavy} bytes; the budget is {b['initialDownloadBytes']}")
        self.summary = {"worstTriangles": tris, "worstDrawCalls": calls, "initialBytes": initial, "heaviestBytes": heavy}


# ---------------------------------------------------------------- helpers


def _read_json(path: str) -> Dict[str, Any]:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _node(g: glb.Glb, name: str) -> Optional[Dict[str, Any]]:
    for n in g.mesh_nodes():
        if n.get("name") == name:
            return n
    return None


def _bounds(g: glb.Glb, nodes: Sequence[Optional[Dict[str, Any]]]) -> Tuple[List[float], List[float]]:
    lo, hi = [1e9] * 3, [-1e9] * 3
    for n in nodes:
        if n is None:
            continue
        for prim in g.primitives(n):
            acc = g.json["accessors"][prim["attributes"]["POSITION"]]
            lo = [min(a, b) for a, b in zip(lo, acc["min"])]
            hi = [max(a, b) for a, b in zip(hi, acc["max"])]
    return lo, hi


def _region_extent(g: glb.Glb, node: Optional[Dict[str, Any]]) -> Optional[Tuple[float, float]]:
    if node is None:
        return None
    lo, hi = _bounds(g, [node])
    return lo[1], hi[1]


def _close(a: Sequence[float], b: Sequence[float]) -> bool:
    return len(a) == len(b) and all(abs(x - y) <= REST_TOLERANCE for x, y in zip(a, b))


def main(argv: List[str]) -> int:
    repo = argv[1] if len(argv) > 1 else os.path.dirname(os.path.dirname(HERE))
    checker = Checker(repo)
    errors = checker.run()
    if errors:
        for e in errors:
            sys.stderr.write(f"error: {e}\n")
        sys.stderr.write(f"{len(errors)} asset error(s)\n")
        return 1
    s = getattr(checker, "summary", {})
    sys.stdout.write(
        "assets ok: worst-case avatar {worstTriangles} triangles, {worstDrawCalls} draw calls; initial download "
        "{initialBytes} bytes, heaviest outfit {heaviestBytes} bytes\n".format(**s)
    )
    r = getattr(checker, "room_summary", None)
    if r:
        sys.stdout.write(
            "room ok: {roomItems} room items; worst-case room {roomTriangles} triangles, {roomDrawCalls} draw calls, "
            "{roomBytes} bytes; shell {shellBytes} bytes\n".format(**r)
        )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
