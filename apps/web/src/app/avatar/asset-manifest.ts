import avatar3d from "../../../../../assets/manifests/avatar-3d.v1.json";
import catalog from "../../../../../assets/manifests/catalog.v1.json";

/**
 * The generated art manifests (assets/source/build.sh, spec §9), read at build time: asset URLs are
 * same-origin, immutable paths (`/assets/3d/...`) that only the pipeline writes, never player input.
 */
export interface TextureVariant {
  material: string;
  color: string;
}

export interface ItemAsset {
  sku: string;
  slot: string;
  name: string;
  assetUrl: string;
  thumbnailUrl: string;
  bodyCompatibility: string[];
  attachBone?: string;
  hideBodyRegions: string[];
  textureVariants: TextureVariant[];
  starter?: boolean;
  triangles: number;
  downloadBytes: number;
}

export interface Avatar3d {
  rigVersion: string;
  bones: string[];
  regions: Record<string, string[]>;
  base: { assetUrl: string; triangles: number; downloadBytes: number };
  bodyMorph: string;
  bodyPresets: Record<string, { morph: number }>;
  skinTones: Record<string, string>;
  hairColors: Record<string, string>;
  faces: string[];
  hairs: string[];
  idleAnimation: string;
  starterItems: Record<string, string>;
  budgets: { avatarTriangles: number; initialDownloadBytes: number; textureMaxPx: number; drawCalls: number };
}

export type AssetLookup = (sku: string) => ItemAsset | undefined;

export const AVATAR_3D: Avatar3d = avatar3d;

const BY_SKU: ReadonlyMap<string, ItemAsset> = new Map(catalog.items.map((item) => [item.sku, item as ItemAsset]));

export const itemAsset: AssetLookup = (sku) => BY_SKU.get(sku);

/** The pipeline thumbnail of an item; the API preview URL when the web build does not know the sku. */
export function itemPicture(sku: string, previewUrl: string): string {
  return BY_SKU.get(sku)?.thumbnailUrl ?? previewUrl;
}
