import * as THREE from "three";
import type { GLTF } from "three/examples/jsm/loaders/GLTFLoader.js";
import { cells, isWall, type Placement, type RoomState } from "./room-layout";
import { ROOM, type RoomItemAsset, roomItem } from "./room-manifest";

/**
 * Room scene parts shared by the My Room editor (`RoomRenderer`) and the live room view of the spaces page
 * (docs/design/p2-room-visits.md §8): flat toon materials, item clones with their colors, placeholder boxes and
 * placement transforms. glTF space: Y up, the back wall at z = −2 m, the left wall at x = −2 m.
 */

const HALF = (ROOM.floor.columns * ROOM.cellMeters) / 2;
const CELL = ROOM.cellMeters;

export function materialsOf(object: THREE.Object3D): THREE.Material[] {
  const mesh = object as THREE.Mesh;
  if (!mesh.isMesh) return [];
  return Array.isArray(mesh.material) ? mesh.material : [mesh.material];
}

export function materialRole(material: THREE.Material): string {
  return material.name.split(".")[0] ?? material.name;
}

/** A toon copy of a file material. No normals in the files (flat low-poly look): the shader derives face normals. */
export function toonMaterial(source: THREE.Material, gradient: THREE.Texture): THREE.MeshToonMaterial {
  const standard = source as THREE.MeshStandardMaterial;
  const material = new THREE.MeshToonMaterial({
    name: materialRole(source),
    color: standard.color?.clone() ?? new THREE.Color(0xffffff),
    emissive: standard.emissive?.clone() ?? new THREE.Color(0x000000),
    side: source.side,
    gradientMap: gradient,
  });
  return Object.assign(material, { flatShading: true });
}

export function toonify(object: THREE.Object3D, gradient: THREE.Texture): void {
  const mesh = object as THREE.Mesh;
  if (!mesh.isMesh) return;
  mesh.material = Array.isArray(mesh.material) ? mesh.material.map((m) => toonMaterial(m, gradient)) : toonMaterial(mesh.material, gradient);
}

/** A clone with its own toon materials (geometry stays shared with the cached file) and the sku's colors. */
export function cloneItem(gltf: GLTF, asset: RoomItemAsset, gradient: THREE.Texture): THREE.Object3D {
  const object = gltf.scene.clone(true);
  const colors = new Map(asset.textureVariants.map((v) => [v.material, v.color]));
  object.traverse((o) => {
    toonify(o, gradient);
    for (const m of materialsOf(o)) {
      const color = colors.get(materialRole(m));
      if (color !== undefined) (m as THREE.MeshToonMaterial).color.set(color);
    }
  });
  return object;
}

/** A neutral box of the item's footprint, for a file that did not load. */
export function placeholderItem(asset: RoomItemAsset, p: Placement, gradient: THREE.Texture): THREE.Object3D {
  const [w, d] = cells(asset, p.surface, 0);
  const size = isWall(p.surface) ? [w * CELL, d * CELL, 0.1] : [w * CELL, 0.5, d * CELL];
  const mesh = new THREE.Mesh(
    new THREE.BoxGeometry(size[0]! - 0.04, size[1]! - 0.04, size[2]! - 0.02),
    toonMaterial(new THREE.MeshStandardMaterial({ color: 0x8a90a6 }), gradient),
  );
  mesh.position.set(0, size[1]! / 2, isWall(p.surface) ? 0.05 : 0);
  mesh.userData["placeholder"] = true;
  const group = new THREE.Group();
  group.add(mesh);
  return group;
}

/** Position and turn (radians around +Y) of a placement. */
export function transformOf(p: Placement, asset: RoomItemAsset): { position: [number, number, number]; turn: number } {
  const [w, d] = cells(asset, p.surface, p.rotation);
  if (p.surface === "floor") {
    return { position: [-HALF + CELL * (p.x + w / 2), 0, -HALF + CELL * (p.y + d / 2)], turn: (p.rotation * Math.PI) / 180 };
  }
  const along = -HALF + CELL * (p.x + w / 2);
  return p.surface === "wall_back"
    ? { position: [along, CELL * p.y, -HALF], turn: 0 }
    : { position: [-HALF, CELL * p.y, along], turn: Math.PI / 2 };
}

export interface RoomScenery {
  group: THREE.Group;
  /** Skus whose file did not load (shown as boxes, or the default finish). */
  failed: string[];
}

/**
 * A whole room as one group (the live room view): a shell clone, the finishes and the placed items. Rejects when the
 * shell does not load. `load` caches files; the clones share geometry with the cache, so dispose only materials.
 */
export async function buildRoomScenery(room: RoomState, load: (url: string) => Promise<GLTF>, gradient: THREE.Texture): Promise<RoomScenery> {
  const shellFile = await load(ROOM.shell.assetUrl);
  const finishes = [room.wallItem, room.floorItem].flatMap((ref) => {
    const asset = ref === null ? undefined : roomItem(ref.sku);
    return asset === undefined ? [] : [asset];
  });
  const placed = room.items.flatMap((p) => {
    const asset = roomItem(p.sku);
    return asset === undefined ? [] : [{ p, asset }];
  });
  const loaded = new Map<string, GLTF | null>();
  await Promise.all(
    [...new Set([...finishes, ...placed.map((x) => x.asset)].map((a) => a.assetUrl))].map((url) =>
      load(url).then(
        (gltf) => loaded.set(url, gltf),
        () => loaded.set(url, null),
      ),
    ),
  );

  const group = new THREE.Group();
  const shell = shellFile.scene.clone(true);
  shell.traverse((o) => toonify(o, gradient));
  group.add(shell);
  const failed: string[] = [];
  const replaced = new Set<string>();
  for (const asset of finishes) {
    const gltf = loaded.get(asset.assetUrl);
    if (!gltf) {
      failed.push(asset.sku);
      continue;
    }
    group.add(cloneItem(gltf, asset, gradient));
    if (asset.replaces) replaced.add(asset.replaces);
  }
  const walls = shell.getObjectByName(ROOM.shell.nodes.walls);
  const floor = shell.getObjectByName(ROOM.shell.nodes.floor);
  if (walls) walls.visible = !replaced.has("walls");
  if (floor) floor.visible = !replaced.has("floor");
  for (const { p, asset } of placed) {
    const gltf = loaded.get(asset.assetUrl);
    const object = gltf ? cloneItem(gltf, asset, gradient) : placeholderItem(asset, p, gradient);
    if (!gltf) failed.push(asset.sku);
    const { position, turn } = transformOf(p, asset);
    object.position.set(...position);
    object.rotation.y = turn;
    group.add(object);
  }
  return { group, failed };
}
