import room from "../../../../../assets/manifests/room.v1.json";

/**
 * The generated room manifest (assets/source/build.sh, docs/design/p1-my-room.md), read at build time. Asset URLs
 * are same-origin, immutable paths that only the pipeline writes, never player input.
 */
export type Surface = "floor" | "wall_back" | "wall_left";
export type Rotation = 0 | 90 | 180 | 270;

export interface RoomItemAsset {
  sku: string;
  slot: string;
  name: string;
  assetUrl: string;
  thumbnailUrl: string;
  /** Cells: `{w, d}` on the floor, `{w, h}` on a wall; none for finishes. */
  footprint?: { w: number; d?: number; h?: number };
  layer?: "rug" | "object";
  surfaces: string[];
  /** Finishes: the shell node they replace. */
  replaces?: "walls" | "floor";
  textureVariants: { material: string; color: string }[];
  triangles: number;
  drawCalls: number;
  downloadBytes: number;
}

export interface RoomManifest {
  cellMeters: number;
  floor: { columns: number; rows: number };
  wall: { columns: number; rows: number; heightMeters: number };
  maxItems: number;
  shell: { assetUrl: string; nodes: { floor: string; walls: string; trim: string } };
  items: RoomItemAsset[];
}

export const ROOM: RoomManifest = room as RoomManifest;

const BY_SKU: ReadonlyMap<string, RoomItemAsset> = new Map(ROOM.items.map((item) => [item.sku, item]));

export type RoomLookup = (sku: string) => RoomItemAsset | undefined;

export const roomItem: RoomLookup = (sku) => BY_SKU.get(sku);
