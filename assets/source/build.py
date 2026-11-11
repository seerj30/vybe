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
from vybe_art import body, catalog, emotes, palette, rig, room  # noqa: E402
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


# ---------------------------------------------------------------- My Room (docs/design/p1-my-room.md)

ROOM_YAW = 35.0  # the camera looks from the front right toward the back-left corner (both walls in view)
ROOM_PITCH = -30.0


def shell_objects() -> dict:
    sm = room.SHELL_MATS
    return {o.name: o for o in (B.static_object(room.shell_floor(), sm), B.static_object(room.shell_walls(), sm),
                                B.static_object(room.shell_trim(), sm))}


def room_item_object(item_or_asset, colors: dict | None = None):
    asset = item_or_asset if isinstance(item_or_asset, room.RoomAsset) else room.ROOM_ASSET_BY_ID[item_or_asset.asset]
    obj = B.static_object(asset.build(), asset.mats, name=asset.id)
    if colors:
        B.recolor(obj, colors)
    return obj


def build_room_shell(out: str) -> None:
    B.reset()
    shell_objects()
    B.export_glb(out)


def build_room_asset(asset: room.RoomAsset, out: str) -> None:
    B.reset()
    room_item_object(asset)
    B.export_glb(out)


def room_with_finish(item: room.RoomItem) -> list:
    """The shell with one finish in place of the shell walls or floor."""
    asset = room.ROOM_ASSET_BY_ID[item.asset]
    shell = shell_objects()
    finish = room_item_object(item, item.colors)
    replaced = "shell_walls" if asset.slot == "room_wall" else "shell_floor"
    B.hide([shell[replaced]])
    return [o for name, o in shell.items() if name != replaced] + [finish]


def room_thumbnail(item: room.RoomItem, out: str) -> None:
    B.reset()
    asset = room.ROOM_ASSET_BY_ID[item.asset]
    if asset.footprint is None:
        B.render_thumbnail(out, room_with_finish(item), yaw=ROOM_YAW, pitch=ROOM_PITCH)
    else:
        B.render_thumbnail(out, [room_item_object(item, item.colors)], yaw=30.0, pitch=-22.0)


# A furnished room for the PR and the docs (not published): (sku, surface, x, y, rotation).
ROOM_PREVIEW = {
    "finishes": ["walls_brick", "floor_oak"],
    "items": [
        ("furn_bed", "floor", 0, 0, 0), ("furn_round_rug", "floor", 3, 3, 0), ("furn_sofa", "floor", 4, 6, 180),
        ("furn_coffee_table", "floor", 4, 4, 0), ("furn_hoops_arcade", "floor", 6, 0, 0),
        ("furn_bookshelf", "floor", 0, 4, 90), ("furn_potted_plant", "floor", 3, 0, 0),
        ("furn_floor_lamp", "floor", 0, 6, 0), ("furn_beanbag", "floor", 1, 6, 0),
        ("furn_trophy_stand", "floor", 5, 0, 0), ("decor_hoops_poster", "wall_back", 3, 2, 0),
        ("decor_neon_bolt", "wall_left", 5, 2, 0), ("decor_wall_clock", "wall_back", 1, 3, 0),
        ("decor_wall_shelf", "wall_left", 2, 2, 0),
    ],
}


def room_preview(folder: str) -> None:
    os.makedirs(folder, exist_ok=True)
    by_sku = {i.sku: i for i in room.ROOM_ITEMS}
    B.reset()
    shell = shell_objects()
    objs = [shell["shell_trim"]]
    for sku in ROOM_PREVIEW["finishes"]:
        objs.append(room_item_object(by_sku[sku], by_sku[sku].colors))
    B.hide([shell["shell_walls"], shell["shell_floor"]])
    for sku, surface, x, y, rotation in ROOM_PREVIEW["items"]:
        item = by_sku[sku]
        asset = room.ROOM_ASSET_BY_ID[item.asset]
        obj = room_item_object(item, item.colors)
        if surface == "floor":
            B.place(obj, *room.place_floor(x, y, asset.footprint, rotation))
        else:
            B.place(obj, *room.place_wall(surface, x, y, asset.footprint))
        objs.append(obj)
    B.render_thumbnail(os.path.join(folder, "room.png"), objs, size=768, yaw=ROOM_YAW, pitch=ROOM_PITCH, line=1.6)


def room_info() -> dict:
    return {
        "version": room.ROOM_VERSION,
        "cellMeters": room.CELL,
        "floor": {"columns": room.FLOOR_CELLS[0], "rows": room.FLOOR_CELLS[1]},
        "wall": {"columns": room.WALL_CELLS[0], "rows": room.WALL_CELLS[1], "heightMeters": room.WALL_HEIGHT},
        "maxItems": room.MAX_ITEMS,
        "surfaces": list(room.SURFACES),
        "rotations": list(room.ROTATIONS),
        "shell": {"file": "room/shell.glb", "nodes": {"floor": "shell_floor", "walls": "shell_walls", "trim": "shell_trim"}},
        "assets": {
            a.id: {"file": f"room/{a.id}.glb", "slot": a.slot, "version": a.version,
                   "footprint": list(a.footprint) if a.footprint else None, "layer": a.layer}
            for a in room.ROOM_ASSETS
        },
        "items": [
            {"sku": i.sku, "name": i.name, "price": i.price, "minLevel": i.min_level, "rarity": i.rarity,
             "asset": i.asset, "assetKey": i.asset_key, "colors": i.colors, "thumb": f"room-thumbs/{i.sku}.png",
             "surfaces": room.surfaces(room.ROOM_ASSET_BY_ID[i.asset].slot)}
            for i in room.ROOM_ITEMS
        ],
    }


def build_room(staging: str, argv: list) -> None:
    os.makedirs(os.path.join(staging, "room"), exist_ok=True)
    os.makedirs(os.path.join(staging, "room-thumbs"), exist_ok=True)
    build_room_shell(os.path.join(staging, "room", "shell.glb"))
    for asset in room.ROOM_ASSETS:
        build_room_asset(asset, os.path.join(staging, "room", f"{asset.id}.glb"))
    log(f"room shell and {len(room.ROOM_ASSETS)} room assets")
    for item in room.ROOM_ITEMS:
        room_thumbnail(item, os.path.join(staging, "room-thumbs", f"{item.sku}.png"))
    log(f"{len(room.ROOM_ITEMS)} room thumbnails")
    if "--previews" in argv:
        room_preview(os.path.join(staging, "previews"))
        log("room preview")


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
    build_room(STAGING, argv)

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
        "room": room_info(),
    }
    with open(os.path.join(STAGING, "build-info.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=2)
    publish.main(STAGING)
    log(f"published in {time.time() - t0:.0f} s")


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(args)
