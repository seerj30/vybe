import type { Schemas } from "@vybe/contracts";
import { AVATAR_3D, type AssetLookup, type Avatar3d, itemAsset } from "./asset-manifest";
import type { AvatarPresets } from "./presets";

type Equipped = Schemas["Equipped"];

/** What the avatar wears, by sku (the viewport never sees item ids). */
export interface Outfit {
  hair: string | null;
  top: string | null;
  bottom: string | null;
  shoes: string | null;
  accessories: readonly [string | null, string | null];
}

/** One item file on the avatar. `node` picks one mesh of the file (accessory attachment point), null = all. */
export interface LookPart {
  key: string;
  sku: string;
  url: string;
  node: string | null;
  colors: Readonly<Record<string, string>>;
}

/** Everything the renderer needs; plain data, so it is easy to test and compare. */
export interface Look {
  baseUrl: string;
  morph: number;
  skinColor: string;
  hairColor: string;
  face: string;
  presetHair: string | null;
  hiddenRegions: readonly string[];
  parts: readonly LookPart[];
  /** Skus that cannot show (unknown to this build, or not for this body), replaced by a starter or left off. */
  skipped: readonly string[];
}

export const EMPTY_OUTFIT: Outfit = { hair: null, top: null, bottom: null, shoes: null, accessories: [null, null] };

/** Regions shown in a neutral base-layer color when no garment covers them (no bare torso). */
export const BASE_LAYER_REGIONS: readonly string[] = ["torso", "hips", "upper_legs"];
export const BASE_LAYER_COLOR = "#3a4466";

const NEUTRAL = "#8a90a6";
const GARMENTS = ["top", "bottom", "shoes"] as const;
type Garment = (typeof GARMENTS)[number];

/** Outfit from the server's equipment (item ids) and the inventory (id → sku). Unknown ids are left off. */
export function outfitFromEquipped(equipped: Equipped, inventory: readonly { itemId: string; sku: string }[]): Outfit {
  const sku = new Map(inventory.map((item) => [item.itemId, item.sku]));
  const of = (id: string | null): string | null => (id === null ? null : (sku.get(id) ?? null));
  return {
    hair: of(equipped.hair),
    top: of(equipped.top),
    bottom: of(equipped.bottom),
    shoes: of(equipped.shoes),
    accessories: [of(equipped.accessories[0] ?? null), of(equipped.accessories[1] ?? null)],
  };
}

/** The outfit with one more item on (shop try-on). Accessories go to a free point, else point 1. */
export function withItem(outfit: Outfit, slot: string, sku: string): Outfit {
  switch (slot) {
    case "hair":
    case "top":
    case "bottom":
    case "shoes":
      return { ...outfit, [slot]: sku };
    case "accessory": {
      const [a, b] = outfit.accessories;
      if (a === sku || b === sku) return outfit;
      return { ...outfit, accessories: a !== null && b === null ? [a, sku] : [sku, b] };
    }
    default:
      return outfit;
  }
}

/** Items that failed to load come off: garments fall back to the starter item, others to nothing (spec §9). */
export function fallbackOutfit(outfit: Outfit, failed: ReadonlySet<string>, manifest: Avatar3d = AVATAR_3D): Outfit {
  const keep = (sku: string | null): string | null => (sku !== null && failed.has(sku) ? null : sku);
  const garment = (slot: Garment): string | null => {
    const sku = outfit[slot];
    if (sku === null || !failed.has(sku)) return sku;
    const starter = manifest.starterItems[slot] ?? null;
    return starter !== null && !failed.has(starter) ? starter : null;
  };
  return {
    hair: keep(outfit.hair),
    top: garment("top"),
    bottom: garment("bottom"),
    shoes: garment("shoes"),
    accessories: [keep(outfit.accessories[0]), keep(outfit.accessories[1])],
  };
}

function assetStem(url: string): string {
  const file = url.slice(url.lastIndexOf("/") + 1);
  return file.slice(0, file.indexOf("."));
}

/** Resolves presets and an outfit to files, colors and visibility (spec §9 slots, coverage masks, body presets). */
export function resolveLook(presets: AvatarPresets, outfit: Outfit, manifest: Avatar3d = AVATAR_3D, assets: AssetLookup = itemAsset): Look {
  const body = presets.bodyPresetId;
  const skipped: string[] = [];
  const parts: LookPart[] = [];
  const hidden = new Set<string>();

  const usable = (sku: string | null, slot: string) => {
    if (sku === null) return undefined;
    const asset = assets(sku);
    if (asset === undefined || asset.slot !== slot || !asset.bodyCompatibility.includes(body)) {
      skipped.push(sku);
      return undefined;
    }
    return asset;
  };
  const add = (key: string, sku: string, url: string, node: string | null, colors: Record<string, string>, hide: readonly string[]) => {
    parts.push({ key, sku, url, node, colors });
    for (const region of hide) hidden.add(region);
  };
  const colorsOf = (variants: readonly { material: string; color: string }[]) => Object.fromEntries(variants.map((v) => [v.material, v.color]));

  for (const slot of GARMENTS) {
    let asset = usable(outfit[slot], slot);
    if (asset === undefined && outfit[slot] !== null) asset = usable(manifest.starterItems[slot] ?? null, slot);
    if (asset !== undefined) add(slot, asset.sku, asset.assetUrl, null, colorsOf(asset.textureVariants), asset.hideBodyRegions);
  }
  outfit.accessories.forEach((sku, index) => {
    const asset = usable(sku, "accessory");
    if (asset === undefined) return;
    const point = asset.attachBone?.endsWith("_L") ? index + 1 : 1;
    add(`accessory-${index}`, asset.sku, asset.assetUrl, `${assetStem(asset.assetUrl)}_point_${point}`, colorsOf(asset.textureVariants), asset.hideBodyRegions);
  });
  const hairItem = usable(outfit.hair, "hair");
  if (hairItem !== undefined && !hidden.has("hair")) {
    add("hair", hairItem.sku, hairItem.assetUrl, null, colorsOf(hairItem.textureVariants), hairItem.hideBodyRegions);
  }

  const presetHair = hairItem === undefined && !hidden.has("hair") && manifest.hairs.includes(presets.hairPresetId) ? presets.hairPresetId : null;
  return {
    baseUrl: manifest.base.assetUrl,
    morph: manifest.bodyPresets[body]?.morph ?? 0,
    skinColor: manifest.skinTones[presets.skinToneId] ?? NEUTRAL,
    hairColor: manifest.hairColors[presets.hairColorId] ?? NEUTRAL,
    face: manifest.faces.includes(presets.facePresetId) ? presets.facePresetId : (manifest.faces[0] ?? ""),
    presetHair,
    hiddenRegions: [...hidden].sort(),
    parts,
    skipped,
  };
}

/** Item files of a look (to download and check). */
export function partUrls(look: Look): string[] {
  return [...new Set(look.parts.map((part) => part.url))];
}
