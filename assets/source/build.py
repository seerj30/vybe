"""VYBE art pipeline entry point. Run it with assets/source/build.sh (Docker, Blender 4.5 LTS bpy module).

Also runs in the Blender app: blender -b --factory-startup -P assets/source/build.py -- [--previews]

Steps: base avatar .glb (rig, 2 body presets as a shape key, faces, preset hair, idle loop), one .glb per
catalog asset, one thumbnail per sku, then assets/tools/publish.py (hashed file names and manifests).
"""

from __future__ import annotations

import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(REPO, "assets", "tools"))

import bpy  # noqa: E402

import publish  # noqa: E402
from vybe_art import blender as B  # noqa: E402
from vybe_art import body, catalog, emotes, palette, rig  # noqa: E402
from vybe_art.catalog import ASSET_BY_ID, Asset, Item  # noqa: E402

STAGING = os.path.join(REPO, "assets", "build")
HEAD_THUMB_SLOTS = {"hair"}


def log(msg: str) -> None:
    sys.stdout.write(f"[art] {msg}\n")
    sys.stdout.flush()


def base_objects(arm: bpy.types.Object) -> dict:
    mats = palette.materials()
    objs = {}
    for a, b in zip(body.base_meshes(rig.BODY_A), body.base_meshes(rig.BODY_B)):
        objs[a.name] = B.mesh_object(arm, a, mats, b if a.name.startswith("region_") else None)
    return objs


def needs_morph(asset: Asset) -> bool:
    return asset.fits_bodies and any(
        i.asset == asset.id and rig.MORPH_BODY_B in i.bodies for i in catalog.ITEMS
    )


def item_objects(arm: bpy.types.Object, asset: Asset, colors: dict | None = None) -> list:
    mesh_a = asset.build(rig.BODY_A)
    mesh_b = asset.build(rig.BODY_B) if needs_morph(asset) else None
    objs = []
    if asset.slot == "accessory":
        objs.append(B.mesh_object(arm, mesh_a, asset.mats, mesh_b, name=f"{asset.id}_point_1"))
        if asset.attach_bone and asset.attach_bone.endswith("_L"):
            ma = mesh_a.mirrored(f"{asset.id}_point_2", rig.mirror_name)
            mb = mesh_b.mirrored(f"{asset.id}_point_2", rig.mirror_name) if mesh_b else None
            objs.append(B.mesh_object(arm, ma, asset.mats, mb, name=f"{asset.id}_point_2"))
    else:
        objs.append(B.mesh_object(arm, mesh_a, asset.mats, mesh_b, name=asset.id))
    if colors:
        for o in objs:
            B.recolor(o, colors)
    return objs


def emote_action(arm: bpy.types.Object, emote: str) -> None:
    B.action(arm, f"emote_{emote}", emotes.EMOTES[emote]())


def build_base(out: str) -> None:
    B.reset()
    arm = B.armature()
    base_objects(arm)
    B.action(arm, "idle", emotes.idle())
    B.export_glb(out, animations=True)


def build_asset(asset: Asset, out: str) -> None:
    B.reset()
    arm = B.armature()
    if asset.emote:
        emote_action(arm, asset.emote)
        B.export_glb(out, animations=True)
    else:
        item_objects(arm, asset)
        B.export_glb(out)


def dressed_avatar(arm: bpy.types.Object, outfit: list, hair: str = "hair_short", face: str = "face_01") -> list:
    """Base avatar with items (skus) on, the regions they hide hidden. Returns the visible objects."""
    base = base_objects(arm)
    hidden = set()
    shown = []
    for sku in outfit:
        item = next(i for i in catalog.ITEMS if i.sku == sku)
        asset = ASSET_BY_ID[item.asset]
        hidden.update(asset.hide)
        objs = item_objects(arm, asset, item.colors)
        shown += [o for o in objs if not o.name.endswith("_point_2")]
        B.hide([o for o in objs if o.name.endswith("_point_2")])
        if asset.slot == "hair":
            hidden.add("hair")
    for name, obj in base.items():
        keep = (
            (name.startswith("region_") and name[len("region_"):] not in hidden)
            or name == face
            or (name == hair and "hair" not in hidden)
        )
        if keep:
            shown.append(obj)
        else:
            B.hide([obj])
    return shown


STARTER_OUTFIT = ["top_starter_tee", "bottom_starter_shorts", "shoes_starter_kicks"]


def thumbnail(item: Item, out: str) -> None:
    asset = ASSET_BY_ID[item.asset]
    B.reset()
    arm = B.armature()
    if asset.emote:
        objs = dressed_avatar(arm, STARTER_OUTFIT)
        emote_action(arm, asset.emote)
        B.pose_at(arm, emotes.PEAK[asset.emote])
        B.render_thumbnail(out, objs)
        return
    made = item_objects(arm, asset, item.colors)
    objs = [o for o in made if not o.name.endswith("_point_2")]
    B.hide([o for o in made if o not in objs])
    if asset.slot in HEAD_THUMB_SLOTS or asset.attach_bone == "head":
        base = base_objects(arm)
        context = [base["region_head"], base["face_01"]]
        B.hide([o for o in base.values() if o not in context])
        objs += context
    B.render_thumbnail(out, objs)


PREVIEWS = {
    "starter": STARTER_OUTFIT,
    "court": ["top_court_jersey", "bottom_court_shorts", "shoes_gold_high", "acc_sweatband", "hair_high_top"],
    "street": ["top_varsity_jacket", "bottom_cargo_pants", "shoes_trail_boots", "acc_snapback"],
    "epic": ["top_galaxy_jacket", "bottom_gold_track", "shoes_glow_kicks", "acc_court_crown", "hair_flame_top"],
}


def previews(folder: str) -> None:
    os.makedirs(folder, exist_ok=True)
    for name, outfit in PREVIEWS.items():
        for body_id in ("body_a", "body_b"):
            B.reset()
            arm = B.armature()
            objs = dressed_avatar(arm, outfit)
            for o in objs:
                if o.data.shape_keys:
                    o.data.shape_keys.key_blocks[rig.MORPH_BODY_B].value = 1.0 if body_id == "body_b" else 0.0
            B.render_thumbnail(os.path.join(folder, f"{name}-{body_id}.png"), objs, size=512)


def main(argv: list) -> None:
    t0 = time.time()
    os.makedirs(os.path.join(STAGING, "items"), exist_ok=True)
    os.makedirs(os.path.join(STAGING, "thumbs"), exist_ok=True)
    build_base(os.path.join(STAGING, "base.glb"))
    log("base avatar")
    for asset in catalog.ASSETS:
        build_asset(asset, os.path.join(STAGING, "items", f"{asset.id}.glb"))
    log(f"{len(catalog.ASSETS)} assets")
    for item in catalog.ITEMS:
        thumbnail(item, os.path.join(STAGING, "thumbs", f"{item.sku}.png"))
    log(f"{len(catalog.ITEMS)} thumbnails")
    if "--previews" in argv:
        previews(os.path.join(STAGING, "previews"))
        log("previews")

    info = {
        "catalogVersion": catalog.CATALOG_VERSION,
        "rigVersion": rig.RIG_VERSION,
        "bones": rig.BONE_NAMES,
        "regions": rig.REGIONS,
        "attachBones": rig.ATTACH_BONES,
        "requiredMasks": rig.REQUIRED_MASKS,
        "bodyMorph": rig.MORPH_BODY_B,
        "bodyPresets": {k: {"morph": 1.0 if k == "body_b" else 0.0} for k in rig.BODY_PRESETS},
        "skinTones": palette.SKIN_TONES,
        "hairColors": palette.HAIR_COLORS,
        "runtimeMaterials": palette.RUNTIME_MATERIALS,
        "faces": body.FACES,
        "hairs": body.PRESET_HAIRS,
        "idleAnimation": "idle",
        "starterItems": catalog.STARTER_BY_SLOT,
        "base": {"file": "base.glb"},
        "assets": {
            a.id: {
                "file": f"items/{a.id}.glb",
                "slot": a.slot,
                "hide": list(a.hide),
                "attachBone": a.attach_bone,
                "version": a.version,
            }
            for a in catalog.ASSETS
        },
        "items": [
            {
                "sku": i.sku,
                "name": i.name,
                "price": i.price,
                "minLevel": i.min_level,
                "rarity": i.rarity,
                "asset": i.asset,
                "assetKey": i.asset_key,
                "bodies": list(i.bodies),
                "starter": i.starter,
                "colors": i.colors,
                "thumb": f"thumbs/{i.sku}.png",
            }
            for i in catalog.ITEMS
        ],
    }
    with open(os.path.join(STAGING, "build-info.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=2)
    publish.main(STAGING)
    log(f"published in {time.time() - t0:.0f} s")


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(args)
