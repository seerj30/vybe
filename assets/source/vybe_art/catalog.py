"""The catalog: one entry per sku (spec §8, decision 5 price bands) and the asset (mesh file) it uses.

To add an item, see docs/art/README.md. Never remove or rename a sku that players can own; set a new
assetVersion when the art of an asset changes.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Tuple

from . import wardrobe as w
from .geom import Mesh
from .palette import Material, materials
from .rig import Dims

CATALOG_VERSION = 3
BOTH = ("body_a", "body_b")


@dataclass(frozen=True)
class Asset:
    """One .glb file. build(dims) returns the mesh; it runs for body A and B (shape key) when fits_bodies."""

    id: str
    slot: str
    build: Callable[[Dims], Mesh]
    mats: Dict[str, Material]
    hide: Tuple[str, ...] = ()
    attach_bone: Optional[str] = None
    fits_bodies: bool = True
    version: int = 1
    emote: Optional[str] = None


@dataclass(frozen=True)
class Item:
    sku: str
    name: str
    price: int
    rarity: str
    min_level: int
    asset: str
    asset_key: str
    bodies: Tuple[str, ...] = BOTH
    starter: bool = False
    colors: Dict[str, str] = field(default_factory=dict)  # textureVariants: material -> color


def _top(**kw: object) -> Callable[[Dims], Mesh]:
    return lambda d: w.top(d, "item", **kw)  # type: ignore[arg-type]


def _bottom(**kw: object) -> Callable[[Dims], Mesh]:
    return lambda d: w.bottom(d, "item", **kw)  # type: ignore[arg-type]


def _shoe(**kw: object) -> Callable[[Dims], Mesh]:
    return lambda d: w.shoe(d, "item", **kw)  # type: ignore[arg-type]


def _hair(kind: str) -> Callable[[Dims], Mesh]:
    return lambda _d: w.hair_item(kind, "item")


STRIPE_ZS = [0.80, 0.835, 0.87, 0.905, 0.94, 0.975, 1.01, 1.05, 1.10, 1.15]
TOP_ARMS = ("torso", "upper_arms")
TOP_LONG = ("torso", "upper_arms", "lower_arms")
LEGS_SHORT = ("hips", "upper_legs")
LEGS_LONG = ("hips", "upper_legs", "lower_legs")

ASSETS: List[Asset] = [
    # Tops
    Asset("starter_tee", "top", _top(), materials(primary="#e9edf5", trim="#4f5b82"), TOP_ARMS),
    Asset("court_jersey", "top",
          _top(sleeves="none", tank=True, body=lambda z: "secondary" if 0.96 < z < 1.05 else "primary"),
          materials(primary="#2f6fed", secondary="#ffffff", trim="#ffc83d"), ("torso",)),
    Asset("striped_tank", "top",
          _top(sleeves="none", tank=True, zs_override=STRIPE_ZS,
               body=lambda z: "secondary" if int(round((z - 0.8175) / 0.035)) % 2 == 1 and z < 1.03 else "primary"),
          materials(primary="#f4f1de", secondary="#e07a5f", trim="#3d405b"), ("torso",)),
    Asset("long_sleeve", "top", _top(sleeves="long", cuff="trim"), materials(primary="#7b61ff", trim="#f2f2f2"),
          TOP_LONG),
    Asset("hoodie", "top",
          _top(sleeves="long", fit=0.024, scale=1.08, hem=0.775, hood=True, pocket=True, cuff="trim"),
          materials(primary="#2ee59d", trim="#14213d"), TOP_LONG),
    Asset("varsity_jacket", "top",
          _top(sleeves="long", fit=0.024, scale=1.08, hem=0.78, sleeve="secondary", cuff="trim", zipper="trim",
               collar=True),
          materials(primary="#8d1b3d", secondary="#f4ede1", trim="#1d1d1f"), TOP_LONG),
    Asset("windbreaker", "top",
          _top(sleeves="long", fit=0.026, scale=1.09, hem=0.78, collar=True, zipper="trim",
               body=lambda z: "primary" if z < 0.95 else ("secondary" if z < 1.06 else "accent")),
          materials(primary="#1b998b", secondary="#f7b32b", accent="#2d3047", trim="#ffffff"), TOP_LONG),
    Asset("puffer_jacket", "top",
          _top(sleeves="long", fit=0.034, scale=1.1, hem=0.78, collar=True, zipper="trim", puffy=0.014),
          materials(primary="#ff9f1c", trim="#2b2d42"), TOP_LONG),
    Asset("galaxy_jacket", "top",
          _top(sleeves="long", fit=0.024, scale=1.08, hem=0.78, sleeve="secondary", cuff="trim", zipper="trim",
               collar=True, stars="star"),
          materials(primary="#2a1458", secondary="#120a2e", trim="#7df9ff|#29d5ff", star="#fff3a3|#ffe14d"), TOP_LONG),
    # Bottoms
    Asset("starter_shorts", "bottom", _bottom(), materials(primary="#4f5b82", trim="#2b3350"), LEGS_SHORT),
    Asset("court_shorts", "bottom", _bottom(flare=0.016, stripe="secondary"),
          materials(primary="#e63946", secondary="#f1faee", trim="#1d3557"), LEGS_SHORT),
    Asset("joggers", "bottom", _bottom(length="long", cuff="trim"), materials(primary="#6c757d", trim="#343a40"),
          LEGS_LONG),
    Asset("cargo_pants", "bottom", _bottom(length="long", leg_fit=0.024, flare=0.006, pockets="secondary"),
          materials(primary="#606c38", secondary="#4a5328", trim="#283618"), LEGS_LONG),
    Asset("track_pants", "bottom", _bottom(length="long", stripe="secondary", cuff="primary"),
          materials(primary="#1d3557", secondary="#f1faee", trim="#1d3557"), LEGS_LONG),
    # Shoes
    Asset("starter_kicks", "shoes", _shoe(toe="secondary"),
          materials(primary="#f2f2f2", secondary="#4f5b82", sole="#c9cfdb", trim="#4f5b82"), ("feet",)),
    Asset("high_tops", "shoes", _shoe(kind="high", collar="trim"),
          materials(primary="#e0b03a", sole="#fafafa", trim="#fafafa"), ("feet",)),
    Asset("runners", "shoes", _shoe(stripe="secondary", toe="primary"),
          materials(primary="#00b4d8", secondary="#ffffff", sole="#023e8a", trim="#ffffff"), ("feet",)),
    Asset("slides", "shoes", _shoe(kind="slide"), materials(primary="#3a86ff", sole="#f8f9fa"), ("feet",)),
    Asset("trail_boots", "shoes", _shoe(kind="boot", collar="secondary", laces="trim"),
          materials(primary="#8b5e34", secondary="#3c2a1e", sole="#2b2118", trim="#e9c46a"), ("feet",)),
    Asset("glow_kicks", "shoes", _shoe(stripe="glow_b", toe="primary"),
          materials(primary="#1b1b2f", sole="#39ff14|#2bff00", glow_b="#ff2bd6|#ff00c8", trim="#39ff14|#2bff00"),
          ("feet",)),
    # Hair slot (the "hair" material takes the player's hair color)
    Asset("high_top_hair", "hair", _hair("high_top"), materials(), fits_bodies=False),
    Asset("mohawk", "hair", _hair("mohawk"), materials(), fits_bodies=False),
    Asset("top_bun", "hair", _hair("bun"), materials(accent="#ffc83d"), fits_bodies=False),
    Asset("spikes", "hair", _hair("spikes"), materials(), fits_bodies=False),
    Asset("braids", "hair", _hair("braids"), materials(accent="#ffc83d"), fits_bodies=False),
    Asset("flame_top", "hair", _hair("flame"), materials(flame_a="#ff6b1a|#c43c00", flame_b="#ffd23f|#ff9f00"),
          fits_bodies=False),
    # Accessories
    Asset("sweatband", "accessory", lambda d: w.sweatband(d, "item"), materials(primary="#ffffff", accent="#e63946"),
          attach_bone="lower_arm_L"),
    Asset("chain", "accessory", lambda d: w.chain(d, "item"), materials(metal="#f2c94c", gem="#ffffff|#9be7ff"),
          attach_bone="chest"),
    Asset("snapback", "accessory", lambda _d: w.snapback("item"), materials(primary="#ef476f", secondary="#073b4c"),
          hide=("hair",), attach_bone="head", fits_bodies=False),
    Asset("beanie", "accessory", lambda _d: w.beanie("item"), materials(primary="#ffd166", secondary="#f78c6b"),
          hide=("hair",), attach_bone="head", fits_bodies=False),
    Asset("shades", "accessory", lambda _d: w.shades("item"), materials(primary="#1c1c22", lens="#2b2d42|#14182b"),
          attach_bone="head", fits_bodies=False),
    Asset("headphones", "accessory", lambda _d: w.headphones("item"),
          materials(primary="#e5e5e5", secondary="#ff006e"), attach_bone="head", fits_bodies=False),
    Asset("backpack", "accessory", lambda d: w.backpack(d, "item"),
          materials(primary="#118ab2", secondary="#073b4c"), attach_bone="chest"),
    Asset("court_crown", "accessory", lambda _d: w.crown("item"),
          materials(metal="#ffd23f|#5c4500", gem="#ff4d6d|#ff0040"), attach_bone="head", fits_bodies=False),
    # Catalog version 3 assets (stream Q).
    Asset("aurora_puffer", "top",
          _top(sleeves="long", fit=0.034, scale=1.1, hem=0.78, collar=True, zipper="trim", puffy=0.014),
          materials(primary="#1d2b64", trim="#7df9ff|#00e5ff"), TOP_LONG),
    Asset("neon_joggers", "bottom", _bottom(length="long", cuff="trim", stripe="glow"),
          materials(primary="#111827", glow="#ff2bd6|#ff00c8", trim="#39ff14|#2bff00"), LEGS_LONG),
    Asset("bucket_hat", "accessory", lambda _d: w.bucket_hat("item"),
          materials(primary="#f4a259", secondary="#5b8e7d"), hide=("hair",), attach_bone="head", fits_bodies=False),
    Asset("headband", "accessory", lambda _d: w.headband("item"), materials(primary="#ef476f", accent="#ffffff"),
          attach_bone="head", fits_bodies=False),
    Asset("laser_shades", "accessory", lambda _d: w.shades("item"),
          materials(primary="#e0e0e0", lens="#ff006e|#ff2bd6"), attach_bone="head", fits_bodies=False),
    # Emotes (animation only)
    Asset("swish", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="swish"),
    Asset("wave", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="wave"),
    Asset("cheer", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="cheer"),
    Asset("groove", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="groove"),
    Asset("bow", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="bow"),
    Asset("jump", "emote", lambda _d: Mesh("none"), {}, fits_bodies=False, emote="jump"),
]
ASSET_BY_ID = {a.id: a for a in ASSETS}

ITEMS: List[Item] = [
    # Starter items: free, granted at onboarding (API ContentSeeder, spec §8).
    Item("top_starter_tee", "Starter Tee", 0, "common", 1, "starter_tee", "tops/starter_tee", starter=True),
    Item("bottom_starter_shorts", "Starter Shorts", 0, "common", 1, "starter_shorts", "bottoms/starter_shorts", starter=True),
    Item("shoes_starter_kicks", "Starter Kicks", 0, "common", 1, "starter_kicks", "shoes/starter_kicks", starter=True),
    # Catalog version 1 items (unchanged skus, prices and rules).
    Item("top_court_jersey", "Court Jersey", 500, "common", 1, "court_jersey", "tops/court_jersey"),
    Item("top_neon_hoodie", "Neon Hoodie", 700, "common", 3, "hoodie", "tops/neon_hoodie", bodies=("body_a",)),
    Item("hair_high_top", "High Top", 450, "common", 1, "high_top_hair", "hair/high_top"),
    Item("shoes_gold_high", "Gold Highs", 2500, "rare", 5, "high_tops", "shoes/gold_high"),
    Item("acc_sweatband", "Sweatband", 300, "common", 1, "sweatband", "accessories/sweatband"),
    Item("acc_chain", "Gold Chain", 1500, "rare", 2, "chain", "accessories/chain"),
    Item("emote_swish", "Swish", 400, "common", 1, "swish", "emotes/swish"),
    # Catalog version 2 items.
    Item("top_away_jersey", "Away Jersey", 550, "common", 1, "court_jersey", "tops/away_jersey",
         colors={"primary": "#e63946", "secondary": "#1d3557", "trim": "#f1faee"}),
    Item("top_striped_tank", "Striped Tank", 400, "common", 1, "striped_tank", "tops/striped_tank"),
    Item("top_long_sleeve", "Long Sleeve Tee", 450, "common", 2, "long_sleeve", "tops/long_sleeve"),
    Item("top_sunset_hoodie", "Sunset Hoodie", 750, "common", 4, "hoodie", "tops/sunset_hoodie",
         colors={"primary": "#ff7a45", "trim": "#5b2a86"}),
    Item("top_varsity_jacket", "Varsity Jacket", 2000, "rare", 4, "varsity_jacket", "tops/varsity_jacket"),
    Item("top_windbreaker", "Windbreaker", 1800, "rare", 3, "windbreaker", "tops/windbreaker"),
    Item("top_puffer_jacket", "Puffer Jacket", 2600, "rare", 6, "puffer_jacket", "tops/puffer_jacket"),
    Item("top_galaxy_jacket", "Galaxy Jacket", 8000, "epic", 10, "galaxy_jacket", "tops/galaxy_jacket"),
    Item("bottom_court_shorts", "Court Shorts", 350, "common", 1, "court_shorts", "bottoms/court_shorts"),
    Item("bottom_joggers", "Joggers", 450, "common", 1, "joggers", "bottoms/joggers"),
    Item("bottom_cargo_pants", "Cargo Pants", 600, "common", 2, "cargo_pants", "bottoms/cargo_pants"),
    Item("bottom_track_pants", "Track Pants", 1600, "rare", 3, "track_pants", "bottoms/track_pants"),
    Item("bottom_gold_track", "Gold Track Pants", 6500, "epic", 8, "track_pants", "bottoms/gold_track",
         colors={"primary": "#141414", "secondary": "#ffd23f", "trim": "#141414"}),
    Item("shoes_runners", "Runners", 400, "common", 1, "runners", "shoes/runners"),
    Item("shoes_slides", "Pool Slides", 300, "common", 1, "slides", "shoes/slides"),
    Item("shoes_red_highs", "Red Highs", 650, "common", 2, "high_tops", "shoes/red_highs",
         colors={"primary": "#d62828"}),
    Item("shoes_trail_boots", "Trail Boots", 1700, "rare", 4, "trail_boots", "shoes/trail_boots"),
    Item("shoes_glow_kicks", "Glow Kicks", 7000, "epic", 9, "glow_kicks", "shoes/glow_kicks"),
    Item("hair_mohawk", "Mohawk", 500, "common", 1, "mohawk", "hair/mohawk"),
    Item("hair_top_bun", "Top Bun", 400, "common", 1, "top_bun", "hair/top_bun"),
    Item("hair_spikes", "Spikes", 350, "common", 1, "spikes", "hair/spikes"),
    Item("hair_braids", "Long Braids", 1500, "rare", 3, "braids", "hair/braids"),
    Item("hair_flame_top", "Flame Top", 6000, "epic", 8, "flame_top", "hair/flame_top"),
    Item("acc_snapback", "Snapback", 450, "common", 1, "snapback", "accessories/snapback"),
    Item("acc_beanie", "Beanie", 350, "common", 1, "beanie", "accessories/beanie"),
    Item("acc_shades", "Shades", 400, "common", 1, "shades", "accessories/shades"),
    Item("acc_headphones", "Headphones", 2200, "rare", 4, "headphones", "accessories/headphones"),
    Item("acc_backpack", "Backpack", 1800, "rare", 3, "backpack", "accessories/backpack"),
    Item("acc_court_crown", "Court Crown", 9000, "epic", 12, "court_crown", "accessories/court_crown"),
    Item("emote_wave", "Wave", 300, "common", 1, "wave", "emotes/wave"),
    Item("emote_cheer", "Cheer", 500, "common", 2, "cheer", "emotes/cheer"),
    Item("emote_bow", "Bow", 350, "common", 1, "bow", "emotes/bow"),
    Item("emote_groove", "Groove", 2000, "rare", 5, "groove", "emotes/groove"),
    # Catalog version 3 items (stream Q, P1 "additional cosmetics").
    Item("top_mint_tank", "Mint Stripe Tank", 400, "common", 1, "striped_tank", "tops/mint_tank",
         colors={"primary": "#e8fff6", "secondary": "#2ec4b6", "trim": "#0b3954"}),
    Item("top_gold_hoodie", "Gold Team Hoodie", 700, "common", 2, "hoodie", "tops/gold_hoodie",
         colors={"primary": "#ffd23f", "trim": "#1d3557"}),
    Item("top_forest_windbreaker", "Forest Windbreaker", 1800, "rare", 3, "windbreaker", "tops/forest_windbreaker",
         colors={"primary": "#2d6a4f", "secondary": "#95d5b2", "accent": "#1b4332"}),
    Item("top_aurora_puffer", "Aurora Puffer", 7500, "epic", 9, "aurora_puffer", "tops/aurora_puffer"),
    Item("bottom_denim_shorts", "Denim Shorts", 400, "common", 1, "court_shorts", "bottoms/denim_shorts",
         colors={"primary": "#3a6ea5", "secondary": "#c0d6df", "trim": "#1b263b"}),
    Item("bottom_neon_joggers", "Neon Joggers", 6500, "epic", 8, "neon_joggers", "bottoms/neon_joggers"),
    Item("shoes_classic_highs", "Classic Highs", 600, "common", 1, "high_tops", "shoes/classic_highs",
         colors={"primary": "#f5f5f5", "trim": "#1c1c22"}),
    Item("shoes_lava_boots", "Lava Boots", 1900, "rare", 4, "trail_boots", "shoes/lava_boots",
         colors={"primary": "#3c3c3c", "secondary": "#d62828", "trim": "#ffb703"}),
    Item("acc_bucket_hat", "Bucket Hat", 450, "common", 1, "bucket_hat", "accessories/bucket_hat"),
    Item("acc_headband", "Headband", 300, "common", 1, "headband", "accessories/headband"),
    Item("acc_gold_wristband", "Gold Wristband", 1500, "rare", 3, "sweatband", "accessories/gold_wristband",
         colors={"primary": "#ffd23f", "accent": "#1c1c22"}),
    Item("acc_laser_shades", "Laser Shades", 6000, "epic", 7, "laser_shades", "accessories/laser_shades"),
    Item("emote_jump", "Jump", 450, "common", 2, "jump", "emotes/jump"),
]

STARTER_BY_SLOT = {ASSET_BY_ID[i.asset].slot: i.sku for i in ITEMS if i.starter}
