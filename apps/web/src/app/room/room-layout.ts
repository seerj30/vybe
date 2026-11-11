import { ROOM, type RoomItemAsset, type RoomLookup, type RoomManifest, type Rotation, type Surface, roomItem } from "./room-manifest";

/**
 * My Room placement rules (docs/design/p1-my-room.md §1). The API has the same rules
 * (services/api/src/Vybe.Api/Features/RoomLayout.cs) and decides; these give the player an answer at once.
 */
export interface Placement {
  itemId: string;
  sku: string;
  surface: Surface;
  x: number;
  y: number;
  rotation: Rotation;
}

export interface ItemRef {
  itemId: string;
  sku: string;
}

export interface RoomState {
  wallItem: ItemRef | null;
  floorItem: ItemRef | null;
  items: Placement[];
}

export type LayoutProblem = "incompatible_item" | "placement_out_of_bounds" | "placement_overlap" | "room_item_limit";

export const EMPTY_ROOM: RoomState = { wallItem: null, floorItem: null, items: [] };

export const ROTATIONS: readonly Rotation[] = [0, 90, 180, 270];

export function isWall(surface: Surface): boolean {
  return surface !== "floor";
}

/** Cells covered along x and y: the floor footprint turns with the rotation; a wall footprint is w × h. */
export function cells(asset: RoomItemAsset, surface: Surface, rotation: Rotation): [number, number] {
  const fp = asset.footprint ?? { w: 1, d: 1, h: 1 };
  if (isWall(surface)) return [fp.w, fp.h ?? 1];
  const d = fp.d ?? 1;
  return rotation === 90 || rotation === 270 ? [d, fp.w] : [fp.w, d];
}

function grid(surface: Surface, manifest: RoomManifest): { columns: number; rows: number } {
  return isWall(surface) ? manifest.wall : manifest.floor;
}

/** The group of cells an item competes for: the floor layer (rug or object) or one wall. */
function group(asset: RoomItemAsset, surface: Surface): string {
  return surface === "floor" ? `floor:${asset.layer ?? "object"}` : surface;
}

/** The first problem of a layout, or null. */
export function problemOf(
  items: readonly Placement[],
  lookup: RoomLookup = roomItem,
  manifest: RoomManifest = ROOM,
): { itemId: string; code: LayoutProblem } | null {
  if (items.length > manifest.maxItems) return { itemId: items[manifest.maxItems]!.itemId, code: "room_item_limit" };
  const taken = new Map<string, Set<string>>();
  for (const p of items) {
    const asset = lookup(p.sku);
    if (asset === undefined || asset.footprint === undefined || !asset.surfaces.includes(p.surface) || (isWall(p.surface) && p.rotation !== 0)) {
      return { itemId: p.itemId, code: "incompatible_item" };
    }
    const [w, d] = cells(asset, p.surface, p.rotation);
    const g = grid(p.surface, manifest);
    if (p.x < 0 || p.y < 0 || p.x > g.columns - w || p.y > g.rows - d) return { itemId: p.itemId, code: "placement_out_of_bounds" };
    const key = group(asset, p.surface);
    const used = taken.get(key) ?? new Set<string>();
    taken.set(key, used);
    for (let x = p.x; x < p.x + w; x++) {
      for (let y = p.y; y < p.y + d; y++) {
        const cell = `${x},${y}`;
        if (used.has(cell)) return { itemId: p.itemId, code: "placement_overlap" };
        used.add(cell);
      }
    }
  }
  return null;
}

/** The layout with one placement replaced (or added), if the result is valid; else the problem. */
export function withPlacement(
  items: readonly Placement[],
  next: Placement,
  lookup: RoomLookup = roomItem,
  manifest: RoomManifest = ROOM,
): Placement[] | LayoutProblem {
  const others = items.filter((p) => p.itemId !== next.itemId);
  const index = items.findIndex((p) => p.itemId === next.itemId);
  const result = index < 0 ? [...others, next] : [...others.slice(0, index), next, ...others.slice(index)];
  const problem = problemOf(result, lookup, manifest);
  return problem === null ? result : problem.code;
}

/** The first free place for an item: the floor for furniture, the back wall then the left wall for wall decor. */
export function firstFree(
  items: readonly Placement[],
  item: ItemRef,
  lookup: RoomLookup = roomItem,
  manifest: RoomManifest = ROOM,
): Placement | null {
  const asset = lookup(item.sku);
  if (asset === undefined) return null;
  for (const surface of asset.surfaces as Surface[]) {
    const g = grid(surface, manifest);
    // Walls fill from eye height (row 2) down and up, so posters do not start at the floor.
    const rows = isWall(surface) ? [2, 1, 3, 0, 4] : [...Array(g.rows).keys()];
    for (const y of rows) {
      for (let x = 0; x < g.columns; x++) {
        const next = withPlacement(items, { ...item, surface, x, y, rotation: 0 }, lookup, manifest);
        if (typeof next !== "string") return next.find((p) => p.itemId === item.itemId)!;
      }
    }
  }
  return null;
}

export function moved(p: Placement, dx: number, dy: number): Placement {
  return { ...p, x: p.x + dx, y: p.y + dy };
}

/** Turns a floor item 90° clockwise (seen from above), around its own corner cell. */
export function rotated(p: Placement): Placement {
  return { ...p, rotation: ROTATIONS[(ROTATIONS.indexOf(p.rotation) + 1) % ROTATIONS.length]! };
}

/** A wall item on the other wall, same column and row. */
export function otherWall(p: Placement): Placement {
  return { ...p, surface: p.surface === "wall_back" ? "wall_left" : "wall_back" };
}

/** Text position for screen readers and the list: "floor, column 3, row 2, turned 90°". */
export function describePlacement(p: Placement): string {
  const where = p.surface === "floor" ? "floor" : p.surface === "wall_back" ? "back wall" : "left wall";
  const turn = p.surface === "floor" && p.rotation !== 0 ? `, turned ${p.rotation}°` : "";
  return `${where}, column ${p.x + 1}, row ${p.y + 1}${turn}`;
}

export function fromApi(room: {
  wallItem: ItemRef | null;
  floorItem: ItemRef | null;
  items: readonly { itemId: string; sku: string; surface: Surface; x: number; y: number; rotation: Rotation }[];
}): RoomState {
  return {
    wallItem: room.wallItem === null ? null : { itemId: room.wallItem.itemId, sku: room.wallItem.sku },
    floorItem: room.floorItem === null ? null : { itemId: room.floorItem.itemId, sku: room.floorItem.sku },
    items: room.items.map((p) => ({ itemId: p.itemId, sku: p.sku, surface: p.surface, x: p.x, y: p.y, rotation: p.rotation })),
  };
}

export function toRequest(room: RoomState, expectedRevision: number): {
  expectedRevision: number;
  wallItemId: string | null;
  floorItemId: string | null;
  items: { itemId: string; surface: Surface; x: number; y: number; rotation: Rotation }[];
} {
  return {
    expectedRevision,
    wallItemId: room.wallItem?.itemId ?? null,
    floorItemId: room.floorItem?.itemId ?? null,
    items: room.items.map(({ itemId, surface, x, y, rotation }) => ({ itemId, surface, x, y, rotation })),
  };
}

export function sameRoom(a: RoomState, b: RoomState): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
