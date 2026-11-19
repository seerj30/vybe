import * as THREE from "three";

/**
 * The plaza ground, built in code (no art file): paving tiles inside the 32 m × 32 m walkable square, a curb at
 * the bounds, a flat mosaic in the middle, grass outside, and benches, planters with low-poly trees and lamps
 * outside the curb (P2 has no collision, so nothing stands on the walkable square).
 * Flat neutral colors in the toon style of My Room. Everything is merged into one geometry with vertex colors,
 * so the whole ground is one draw call. Units: meters, y up; the walkable square is x, z in [-16, 16].
 */

const C = {
  tileA: 0xd9d3c7,
  tileB: 0xcac3b4,
  ring: 0xb7ae9c,
  curb: 0x9e978a,
  grass: 0x7fb069,
  grassDark: 0x6a9a58,
  wood: 0xa0703f,
  metal: 0x3a4466,
  leaf: 0x4f9a5a,
  leafLight: 0x6cbf6a,
  trunk: 0x7a5232,
  planter: 0x8c8478,
  lamp: 0xfff1b8,
  mosaicA: 0xe8b04a,
  mosaicB: 0x4d7fb8,
  mosaicStar: 0xf4efe4,
} as const;

const HALF = 16;
const TILE = 2;

export function buildPlazaGround(): THREE.BufferGeometry {
  const parts: THREE.BufferGeometry[] = [];
  const add = (geometry: THREE.BufferGeometry, color: number, matrix?: THREE.Matrix4) => {
    const g = geometry.index === null ? geometry : geometry.toNonIndexed();
    if (matrix !== undefined) g.applyMatrix4(matrix);
    const c = new THREE.Color(color);
    const count = g.getAttribute("position").count;
    const colors = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) colors.set([c.r, c.g, c.b], i * 3);
    g.setAttribute("color", new THREE.BufferAttribute(colors, 3));
    for (const name of Object.keys(g.attributes)) if (name !== "position" && name !== "color") g.deleteAttribute(name);
    parts.push(g);
    if (g !== geometry) geometry.dispose();
  };
  const at = (x: number, y: number, z: number, turn = 0) => new THREE.Matrix4().makeRotationY(turn).setPosition(x, y, z);
  const flat = (w: number, d: number) => new THREE.PlaneGeometry(w, d).rotateX(-Math.PI / 2);

  // Grass around the plaza (drawn larger than the bounds; avatars stay inside).
  add(flat(90, 90), C.grass, at(0, -0.02, 0));
  for (const [x, z] of [[-24, 10], [22, -18], [-20, -26], [26, 24], [6, 30], [-30, -4]] as const) {
    add(new THREE.CircleGeometry(3.2, 7).rotateX(-Math.PI / 2), C.grassDark, at(x, -0.01, z));
  }

  // Paving: a checker of two warm grays. P2 has no collision (protocol §3), so everything inside the walkable
  // square is flat: a mosaic in the middle (the spawn area), and all props stand outside the curb.
  for (let i = 0; i < (2 * HALF) / TILE; i++) {
    for (let j = 0; j < (2 * HALF) / TILE; j++) {
      const x = -HALF + TILE * (i + 0.5);
      const z = -HALF + TILE * (j + 0.5);
      add(flat(TILE, TILE), (i + j) % 2 === 0 ? C.tileA : C.tileB, at(x, 0, z));
    }
  }
  const disc = (radius: number, color: number, y: number, segments = 32) => add(new THREE.CircleGeometry(radius, segments).rotateX(-Math.PI / 2), color, at(0, y, 0));
  disc(6, C.ring, 0.01);
  disc(5.4, C.tileA, 0.02);
  disc(4.2, C.mosaicA, 0.03, 16);
  disc(3.4, C.mosaicB, 0.04, 16);
  for (let k = 0; k < 8; k++) {
    const ray = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(-0.45, 0, 0), new THREE.Vector3(0, 0, 4.1), new THREE.Vector3(0.45, 0, 0)]);
    add(ray, k % 2 === 0 ? C.mosaicStar : C.mosaicA, at(0, 0.05, 0, (k / 8) * Math.PI * 2));
  }
  disc(1.1, C.mosaicStar, 0.06, 16);

  // Curb on the bounds.
  const curb = (w: number, d: number, x: number, z: number) => add(new THREE.BoxGeometry(w, 0.18, d), C.curb, at(x, 0.09, z));
  curb(2 * HALF + 0.6, 0.3, 0, HALF + 0.15);
  curb(2 * HALF + 0.6, 0.3, 0, -HALF - 0.15);
  curb(0.3, 2 * HALF, HALF + 0.15, 0);
  curb(0.3, 2 * HALF, -HALF - 0.15, 0);

  // Outside the curb: benches facing in, planters with low-poly trees, and lamps.
  const bench = (x: number, z: number, turn: number) => {
    // `turn` 0 faces +z. The back is behind the seat.
    const back = new THREE.Vector3(0, 0, -0.24).applyAxisAngle(new THREE.Vector3(0, 1, 0), turn);
    add(new THREE.BoxGeometry(1.8, 0.1, 0.5), C.wood, at(x, 0.45, z, turn));
    add(new THREE.BoxGeometry(1.8, 0.4, 0.08), C.wood, at(x + back.x, 0.7, z + back.z, turn));
    for (const side of [-0.75, 0.75]) {
      const leg = new THREE.Vector3(side, 0, 0).applyAxisAngle(new THREE.Vector3(0, 1, 0), turn);
      add(new THREE.BoxGeometry(0.08, 0.45, 0.45), C.metal, at(x + leg.x, 0.22, z + leg.z, turn));
    }
  };
  const tree = (x: number, z: number, size: number) => {
    add(new THREE.BoxGeometry(1.6, 0.6, 1.6), C.planter, at(x, 0.3, z));
    add(new THREE.CylinderGeometry(0.12, 0.16, 1.2, 6), C.trunk, at(x, 1.2, z));
    add(new THREE.IcosahedronGeometry(0.95 * size, 0), C.leaf, at(x, 2.2 * size + 0.3, z));
    add(new THREE.IcosahedronGeometry(0.6 * size, 0), C.leafLight, at(x + 0.25, 2.75 * size + 0.3, z - 0.15));
  };
  const lamp = (x: number, z: number) => {
    add(new THREE.CylinderGeometry(0.07, 0.1, 3, 6), C.metal, at(x, 1.5, z));
    add(new THREE.IcosahedronGeometry(0.26, 0), C.lamp, at(x, 3.1, z));
  };
  const EDGE = HALF + 1.4;
  for (const along of [-8, 0, 8]) {
    bench(along, EDGE, Math.PI);
    bench(along, -EDGE, 0);
    bench(EDGE, along, -Math.PI / 2);
    bench(-EDGE, along, Math.PI / 2);
  }
  for (const along of [-12, -4, 4, 12]) {
    tree(along, EDGE + 1.6, 1 + ((along + 12) % 7) / 20);
    tree(along, -EDGE - 1.6, 1.05);
    tree(EDGE + 1.6, along, 0.95);
    tree(-EDGE - 1.6, along, 1.1);
  }
  for (const [x, z] of [[-HALF - 0.9, -HALF - 0.9], [HALF + 0.9, -HALF - 0.9], [-HALF - 0.9, HALF + 0.9], [HALF + 0.9, HALF + 0.9]] as const) lamp(x, z);

  const merged = mergeFlat(parts);
  for (const part of parts) part.dispose();
  return merged;
}

function mergeFlat(parts: readonly THREE.BufferGeometry[]): THREE.BufferGeometry {
  const total = parts.reduce((n, g) => n + g.getAttribute("position").count, 0);
  const position = new Float32Array(total * 3);
  const color = new Float32Array(total * 3);
  let offset = 0;
  for (const g of parts) {
    const p = g.getAttribute("position");
    const c = g.getAttribute("color");
    for (let i = 0; i < p.count; i++) {
      position.set([p.getX(i), p.getY(i), p.getZ(i)], (offset + i) * 3);
      color.set([c.getX(i), c.getY(i), c.getZ(i)], (offset + i) * 3);
    }
    offset += p.count;
  }
  const merged = new THREE.BufferGeometry();
  merged.setAttribute("position", new THREE.BufferAttribute(position, 3));
  merged.setAttribute("color", new THREE.BufferAttribute(color, 3));
  merged.computeBoundingSphere();
  return merged;
}
