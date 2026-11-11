"""Publish a pipeline build: content-addressed file names and the manifests (spec §9).

Input: the staging folder that assets/source/build.py writes (build-info.json, .glb files, .png thumbnails).
Output:
  assets/published/3d/...                      immutable files, served by the web container at /assets/3d/...
  assets/manifests/catalog.v1.json             the catalog with the §9 item fields (the API reads it)
  assets/manifests/avatar-3d.v1.json           rig, base avatar file, preset colors, budgets (the web reads it)
  assets/manifests/room.v1.json                My Room grid, shell and room items (the API and the web read it)

Standard library only, so it also runs outside Blender: python3 assets/tools/publish.py <staging>
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import struct
import sys
from typing import Any, Dict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import glb  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
PUBLISHED = os.path.join(REPO, "assets", "published")
URL_ROOT = "/assets"  # same origin; infra/web serves assets/published at /assets
MANIFESTS = os.path.join(REPO, "assets", "manifests")

BUDGETS = {
    "avatarTriangles": 25000,
    "initialDownloadBytes": 2000000,
    "textureMaxPx": 1024,
    "drawCalls": 60,
}

# My Room (docs/design/p1-my-room.md). Item budgets apply to placeable items; finishes have their own limit.
ROOM_BUDGETS = {
    "itemTriangles": 1500,
    "itemDrawCalls": 6,
    "itemDownloadBytes": 120000,
    "finishTriangles": 2500,
    "shellDownloadBytes": 150000,
    "roomTriangles": 50000,
    "roomDrawCalls": 150,
    "roomDownloadBytes": 3000000,
    "footprintToleranceMeters": 0.02,
    "floorItemMaxHeightMeters": 2.4,
    "wallItemMaxDepthMeters": 0.3,
}
FINISH_NODES = {"room_wall": "walls", "room_floor": "floor"}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def strip_png_metadata(data: bytes) -> bytes:
    """Drop text and time chunks (Blender writes the render date and duration), so equal pixels give equal bytes."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG file")
    out = [data[:8]]
    i = 8
    while i < len(data):
        (length,) = struct.unpack(">I", data[i:i + 4])
        kind = data[i + 4:i + 8]
        end = i + 12 + length
        if kind not in (b"tEXt", b"iTXt", b"zTXt", b"tIME"):
            out.append(data[i:end])
        i = end
    return b"".join(out)


def place(src: str, folder: str, stem: str, ext: str) -> Dict[str, Any]:
    """Copy src to published/<folder>/<stem>.<hash12><ext>; returns url, sizes and hash."""
    with open(src, "rb") as f:
        data = f.read()
    if ext == ".png":
        data = strip_png_metadata(data)
    h = digest(data)
    rel = f"{folder}/{stem}.{h[:12]}{ext}"
    dst = os.path.join(PUBLISHED, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, "wb") as f:
        f.write(data)
    return {"url": f"{URL_ROOT}/{rel}", "bytes": len(data), "hash": f"sha256:{h}", "data": data}


def measure(entry: Dict[str, Any]) -> Dict[str, Any]:
    g = glb.read(entry["data"])
    return {
        "triangles": g.triangles(),
        "textureBytes": g.texture_bytes(),
        "downloadBytes": entry["bytes"],
        "contentHash": entry["hash"],
    }


def room_manifest(room: Dict[str, Any], staging: str) -> Dict[str, Any]:
    shell = place(os.path.join(staging, room["shell"]["file"]), "3d/room", "shell", ".glb")
    files: Dict[str, Dict[str, Any]] = {}
    for asset_id, a in room["assets"].items():
        files[asset_id] = place(os.path.join(staging, a["file"]), "3d/room", asset_id, ".glb")
    items = []
    for it in room["items"]:
        a = room["assets"][it["asset"]]
        published = files[it["asset"]]
        thumb = place(os.path.join(staging, it["thumb"]), "3d/thumbs", it["sku"], ".png")
        g = glb.read(published["data"])
        entry: Dict[str, Any] = {
            "sku": it["sku"],
            "slot": a["slot"],
            "name": it["name"],
            "price": it["price"],
            "minLevel": it["minLevel"],
            "rarity": it["rarity"],
            "assetKey": it["assetKey"],
            "assetVersion": a["version"],
            "assetUrl": published["url"],
            "thumbnailUrl": thumb["url"],
            "surfaces": it["surfaces"],
        }
        fp = a["footprint"]
        if fp is not None:
            entry["footprint"] = {"w": fp[0], "d": fp[1]} if a["slot"] == "furniture" else {"w": fp[0], "h": fp[1]}
        if a["layer"]:
            entry["layer"] = a["layer"]
        if a["slot"] in FINISH_NODES:
            entry["replaces"] = FINISH_NODES[a["slot"]]
        entry["textureVariants"] = [{"material": k, "color": v} for k, v in sorted(it["colors"].items())]
        entry.update(measure(published))
        entry["drawCalls"] = g.draw_calls()
        items.append(entry)
    shell_glb = glb.read(shell["data"])
    return {
        "version": room["version"],
        "description": (
            "My Room (docs/design/p1-my-room.md). GENERATED by assets/source/build.sh from "
            "assets/source/vybe_art/room.py; do not edit by hand. Prices follow the decision 5 bands. Footprints are "
            "in cells: {w, d} on the floor, {w, h} on a wall. The API validates layouts with these footprints."
        ),
        "cellMeters": room["cellMeters"],
        "floor": room["floor"],
        "wall": room["wall"],
        "maxItems": room["maxItems"],
        "surfaces": room["surfaces"],
        "rotations": room["rotations"],
        "shell": {"assetUrl": shell["url"], "nodes": room["shell"]["nodes"], **measure(shell),
                  "drawCalls": shell_glb.draw_calls()},
        "items": items,
        "budgets": ROOM_BUDGETS,
    }


def write_json(path: str, doc: Any) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=2, ensure_ascii=False)
        f.write("\n")


def main(staging: str) -> None:
    with open(os.path.join(staging, "build-info.json"), encoding="utf-8") as f:
        info = json.load(f)
    target = os.path.join(PUBLISHED, "3d")
    if os.path.isdir(target):
        shutil.rmtree(target)

    base = place(os.path.join(staging, info["base"]["file"]), "3d/avatar", "base", ".glb")
    assets: Dict[str, Dict[str, Any]] = {}
    for asset_id, a in info["assets"].items():
        assets[asset_id] = place(os.path.join(staging, a["file"]), "3d/items", asset_id, ".glb")

    items = []
    for it in info["items"]:
        a = info["assets"][it["asset"]]
        published = assets[it["asset"]]
        thumb = place(os.path.join(staging, it["thumb"]), "3d/thumbs", it["sku"], ".png")
        entry: Dict[str, Any] = {
            "sku": it["sku"],
            "slot": a["slot"],
            "name": it["name"],
            "price": it["price"],
            "minLevel": it["minLevel"],
            "assetKey": it["assetKey"],
            "compatibleBodyPresets": it["bodies"],
            "rarity": it["rarity"],
        }
        if it["starter"]:
            entry["starter"] = True
        entry.update({
            "assetVersion": a["version"],
            "assetUrl": published["url"],
            "thumbnailUrl": thumb["url"],
            "rigVersion": info["rigVersion"],
            "bodyCompatibility": it["bodies"],
        })
        if a["attachBone"]:
            entry["attachBone"] = a["attachBone"]
        entry.update({
            "hideBodyRegions": a["hide"],
            "textureVariants": [{"material": k, "color": v} for k, v in sorted(it["colors"].items())],
        })
        entry.update(measure(published))
        items.append(entry)

    write_json(os.path.join(MANIFESTS, "catalog.v1.json"), {
        "catalogVersion": info["catalogVersion"],
        "description": (
            "Catalog (spec §8, §9). GENERATED by assets/source/build.sh from assets/source/vybe_art/catalog.py; "
            "do not edit by hand. Prices follow the decision 5 bands: common 300-800, rare 1500-3000, epic "
            "6000-10000. Items with starter=true cost 0 and are granted at onboarding."
        ),
        "items": items,
    })

    if "room" in info:
        write_json(os.path.join(MANIFESTS, "room.v1.json"), room_manifest(info["room"], staging))

    write_json(os.path.join(MANIFESTS, "avatar-3d.v1.json"), {
        "version": 1,
        "description": (
            "3D avatar (spec §9). GENERATED by assets/source/build.sh; do not edit by hand. Preset ids are the "
            "ids of avatar.v1.json (the API validates presets against that file)."
        ),
        "rigVersion": info["rigVersion"],
        "bones": info["bones"],
        "regions": info["regions"],
        "attachBones": info["attachBones"],
        "requiredMasks": info["requiredMasks"],
        "base": {"assetUrl": base["url"], **{k: v for k, v in measure(base).items()}},
        "bodyMorph": info["bodyMorph"],
        "bodyPresets": info["bodyPresets"],
        "skinTones": info["skinTones"],
        "hairColors": info["hairColors"],
        "runtimeMaterials": info["runtimeMaterials"],
        "faces": info["faces"],
        "hairs": info["hairs"],
        "idleAnimation": info["idleAnimation"],
        "starterItems": info["starterItems"],
        "budgets": BUDGETS,
    })


if __name__ == "__main__":
    main(sys.argv[1])
