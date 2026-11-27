import { cells } from "../room/room-layout";
import { ROOM, type RoomLookup, type RoomManifest, roomItem } from "../room/room-manifest";
import type { Bounds, RoomLayout } from "./protocol";

/**
 * Furniture collision in open rooms (docs/design/p2-room-visits.md §4), the same rules as the API
 * (`RoomCollision.cs`, the authority). Shared vectors (`packages/contracts/fixtures/room-collision.v1.json`) keep the
 * two copies equal, so the predicted own avatar stops where the server stops it.
 */

/** The avatar is a circle of this radius (cm); `RoomCollision.AvatarRadiusCm`. */
export const AVATAR_RADIUS_CM = 20;

/** A floor item's footprint grown by the avatar radius, in space cm. Only the inside blocks; the edges are free. */
export interface Obstacle {
  minX: number;
  minZ: number;
  maxX: number;
  maxZ: number;
}

/** Floor items on the `object` layer, with the rotated footprint. Rugs, wall items and unknown SKUs never block. */
export function roomObstacles(layout: RoomLayout, lookup: RoomLookup = roomItem, manifest: RoomManifest = ROOM): Obstacle[] {
  const cell = Math.round(manifest.cellMeters * 100);
  const half = (manifest.floor.columns * cell) / 2;
  const obstacles: Obstacle[] = [];
  for (const p of layout.items) {
    const asset = lookup(p.sku);
    if (p.surface !== "floor" || asset === undefined || asset.layer === "rug" || asset.footprint === undefined) continue;
    const [w, d] = cells(asset, p.surface, p.rotation);
    obstacles.push({
      minX: -half + p.x * cell - AVATAR_RADIUS_CM,
      minZ: -half + p.y * cell - AVATAR_RADIUS_CM,
      maxX: -half + (p.x + w) * cell + AVATAR_RADIUS_CM,
      maxZ: -half + (p.y + d) * cell + AVATAR_RADIUS_CM,
    });
  }
  return obstacles;
}

/**
 * Moves by (sx, sz) inside the bounds: first along x, then along z, so a diagonal move into an obstacle slides along
 * its edge. An obstacle stops a move that starts outside it at its edge; a point inside an obstacle moves freely.
 * With no obstacles (the plaza) this is a plain clamp.
 */
export function moveAround(
  x: number,
  z: number,
  sx: number,
  sz: number,
  obstacles: readonly Obstacle[],
  bounds: Bounds,
): [number, number] {
  let nx = clamp(x + sx, bounds.minX, bounds.maxX);
  for (const o of obstacles) {
    if (z > o.minZ && z < o.maxZ) {
      if (x <= o.minX && nx > o.minX) nx = o.minX;
      else if (x >= o.maxX && nx < o.maxX) nx = o.maxX;
    }
  }
  let nz = clamp(z + sz, bounds.minZ, bounds.maxZ);
  for (const o of obstacles) {
    if (nx > o.minX && nx < o.maxX) {
      if (z <= o.minZ && nz > o.minZ) nz = o.minZ;
      else if (z >= o.maxZ && nz < o.maxZ) nz = o.maxZ;
    }
  }
  return [nx, nz];
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
