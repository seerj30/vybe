import * as THREE from "three";
import { type GLTF, GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { cells, isWall, type Placement, type RoomState } from "./room-layout";
import { ROOM, type RoomItemAsset, roomItem, type Surface } from "./room-manifest";

/**
 * Three.js scene of My Room (docs/design/p1-my-room.md). Loaded lazily by RoomViewport, like the avatar renderer:
 * flat toon materials, render on demand, explicit dispose. Item files are static meshes; each placement is a clone
 * at its cell. A file that does not load becomes a neutral box of its footprint, and the caller shows a notice.
 */

export interface RoomShowReport {
  /** The shell did not load: the caller shows the 2D plan. */
  shellFailed: boolean;
  /** Skus whose file did not load (shown as boxes). */
  failed: string[];
  /** False when a newer show() replaced this one. */
  current: boolean;
}

export interface RoomStats {
  triangles: number;
  drawCalls: number;
}

export interface Pick {
  surface: Surface;
  x: number;
  y: number;
}

export interface RoomRendererOptions {
  onStats: (stats: RoomStats) => void;
  onPick: (pick: Pick) => void;
  onContextLost: () => void;
  onContextRestored: () => void;
}

const TOON_LEVELS = new Uint8Array([105, 185, 255]);
const HALF = (ROOM.floor.columns * ROOM.cellMeters) / 2;
const CELL = ROOM.cellMeters;
const WALL_H = ROOM.wall.rows * CELL;
const CLICK_SLOP_PX = 6;
const SELECTED = 0x2ee5ff;

function materialsOf(object: THREE.Object3D): THREE.Material[] {
  const mesh = object as THREE.Mesh;
  if (!mesh.isMesh) return [];
  return Array.isArray(mesh.material) ? mesh.material : [mesh.material];
}

function role(material: THREE.Material): string {
  return material.name.split(".")[0] ?? material.name;
}

/** Position and turn (radians around +Y) of a placement; glTF space: Y up, the back wall at z = -2 m, the left wall at x = -2 m. */
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

/** The grid cell under a point on the floor or a wall (glTF space), or null outside the room. */
export function cellAt(surface: Surface, point: THREE.Vector3): Pick | null {
  const col = (v: number) => Math.floor((v + HALF) / CELL);
  const pick =
    surface === "floor"
      ? { surface, x: col(point.x), y: col(point.z) }
      : surface === "wall_back"
        ? { surface, x: col(point.x), y: Math.floor(point.y / CELL) }
        : { surface, x: col(point.z), y: Math.floor(point.y / CELL) };
  const g = isWall(surface) ? ROOM.wall : ROOM.floor;
  return pick.x >= 0 && pick.y >= 0 && pick.x < g.columns && pick.y < g.rows ? pick : null;
}

export class RoomRenderer {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 40);
  private readonly root = new THREE.Group();
  private readonly items = new THREE.Group();
  private readonly loader = new GLTFLoader();
  private readonly files = new Map<string, Promise<GLTF>>();
  private readonly gradient: THREE.DataTexture;
  private readonly pickPlanes: { surface: Surface; mesh: THREE.Mesh }[] = [];
  private readonly selection: THREE.LineSegments;
  private readonly resize: ResizeObserver;
  private readonly raycaster = new THREE.Raycaster();
  private shell: THREE.Object3D | null = null;
  private finishes: THREE.Object3D[] = [];
  private token = 0;
  private frame = 0;
  private disposed = false;
  private lost = false;
  private down: { x: number; y: number } | null = null;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly options: RoomRendererOptions,
  ) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "low-power" });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.gradient = new THREE.DataTexture(TOON_LEVELS, TOON_LEVELS.length, 1, THREE.RedFormat);
    this.gradient.minFilter = THREE.NearestFilter;
    this.gradient.magFilter = THREE.NearestFilter;
    this.gradient.generateMipmaps = false;
    this.gradient.needsUpdate = true;

    this.scene.add(new THREE.AmbientLight(0xffffff, 1.2));
    const key = new THREE.DirectionalLight(0xffffff, 2.2);
    key.position.set(3, 6, 4);
    this.scene.add(key);
    this.scene.add(this.root);
    this.root.add(this.items);
    // A fixed three-quarter view from the open front-right corner: both walls stay in view.
    this.camera.position.set(7, 6.2, 9);
    this.camera.lookAt(0, 0.9, 0);

    // Invisible planes for tap and click targets (the floor and the two walls).
    const plane = (w: number, h: number, surface: Surface, place: (m: THREE.Mesh) => void) => {
      const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ visible: false, side: THREE.DoubleSide }));
      place(mesh);
      this.root.add(mesh);
      this.pickPlanes.push({ surface, mesh });
    };
    plane(2 * HALF, 2 * HALF, "floor", (m) => m.rotateX(-Math.PI / 2));
    plane(2 * HALF, WALL_H, "wall_back", (m) => m.position.set(0, WALL_H / 2, -HALF + 0.001));
    plane(2 * HALF, WALL_H, "wall_left", (m) => {
      m.rotateY(Math.PI / 2);
      m.position.set(-HALF + 0.001, WALL_H / 2, 0);
    });

    this.selection = new THREE.LineSegments(
      new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1)),
      new THREE.LineBasicMaterial({ color: SELECTED, depthTest: false, transparent: true }),
    );
    this.selection.renderOrder = 10;
    this.selection.visible = false;
    this.root.add(this.selection);

    canvas.addEventListener("webglcontextlost", this.onLost);
    canvas.addEventListener("webglcontextrestored", this.onRestored);
    canvas.addEventListener("pointerdown", this.onPointerDown);
    canvas.addEventListener("click", this.onClick);
    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(canvas);
    this.fit();
  }

  /** Shows a room: shell, finishes and placements. Files load once and stay cached. */
  async show(room: RoomState, selected: string | null): Promise<RoomShowReport> {
    const token = ++this.token;
    let shell: GLTF;
    try {
      shell = await this.load(ROOM.shell.assetUrl);
    } catch {
      return { shellFailed: true, failed: [], current: token === this.token };
    }
    const finishes = [room.wallItem, room.floorItem].flatMap((ref) => {
      const asset = ref === null ? undefined : roomItem(ref.sku);
      return asset === undefined ? [] : [{ sku: asset.sku, asset }];
    });
    const placed = room.items.flatMap((p) => {
      const asset = roomItem(p.sku);
      return asset === undefined ? [] : [{ p, asset }];
    });
    const urls = [...finishes.map((f) => f.asset.assetUrl), ...placed.map((x) => x.asset.assetUrl)];
    const loaded = new Map<string, GLTF | null>();
    await Promise.all(
      [...new Set(urls)].map((url) =>
        this.load(url).then(
          (gltf) => loaded.set(url, gltf),
          () => loaded.set(url, null),
        ),
      ),
    );
    if (token !== this.token || this.disposed) return { shellFailed: false, failed: [], current: false };

    this.setShell(shell);
    this.clear();
    const failed: string[] = [];
    const replaced = new Set<string>();
    for (const { asset } of finishes) {
      const gltf = loaded.get(asset.assetUrl);
      if (!gltf) {
        failed.push(asset.sku);
        continue;
      }
      const object = this.clone(gltf, asset);
      this.root.add(object);
      this.finishes.push(object);
      if (asset.replaces) replaced.add(asset.replaces);
    }
    this.shell!.getObjectByName(ROOM.shell.nodes.walls)!.visible = !replaced.has("walls");
    this.shell!.getObjectByName(ROOM.shell.nodes.floor)!.visible = !replaced.has("floor");

    for (const { p, asset } of placed) {
      const gltf = loaded.get(asset.assetUrl);
      const object = gltf ? this.clone(gltf, asset) : this.placeholder(asset, p);
      if (!gltf) failed.push(asset.sku);
      const { position, turn } = transformOf(p, asset);
      object.position.set(...position);
      object.rotation.y = turn;
      object.userData["itemId"] = p.itemId;
      this.items.add(object);
    }
    this.select(selected);
    return { shellFailed: false, failed, current: true };
  }

  /** Draws a frame around the selected placement (or none). */
  select(itemId: string | null): void {
    const object = itemId === null ? undefined : this.items.children.find((o) => o.userData["itemId"] === itemId);
    this.selection.visible = object !== undefined;
    if (object !== undefined) {
      const box = new THREE.Box3().setFromObject(object);
      box.getCenter(this.selection.position);
      box.getSize(this.selection.scale).addScalar(0.04);
    }
    this.request();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.token++;
    cancelAnimationFrame(this.frame);
    this.resize.disconnect();
    this.canvas.removeEventListener("webglcontextlost", this.onLost);
    this.canvas.removeEventListener("webglcontextrestored", this.onRestored);
    this.canvas.removeEventListener("pointerdown", this.onPointerDown);
    this.canvas.removeEventListener("click", this.onClick);
    this.root.traverse((object) => {
      (object as THREE.Mesh).geometry?.dispose();
      for (const m of materialsOf(object)) m.dispose();
    });
    (this.selection.material as THREE.Material).dispose();
    for (const file of this.files.values()) {
      void file.then(
        (gltf) => gltf.scene.traverse((object) => (object as THREE.Mesh).geometry?.dispose()),
        () => undefined,
      );
    }
    this.gradient.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
  }

  // ------------------------------------------------------------------ scene

  private load(url: string): Promise<GLTF> {
    let file = this.files.get(url);
    if (file === undefined) {
      file = this.loader.loadAsync(url);
      this.files.set(url, file);
      file.catch(() => this.files.delete(url)); // a later show() can try again
    }
    return file;
  }

  private setShell(gltf: GLTF): void {
    if (this.shell !== null) return;
    this.shell = gltf.scene;
    this.shell.traverse((object) => this.toonify(object));
    this.root.add(this.shell);
  }

  private clear(): void {
    for (const object of [...this.items.children, ...this.finishes]) {
      object.removeFromParent();
      object.traverse((o) => {
        for (const m of materialsOf(o)) m.dispose();
        if (o.userData["placeholder"]) (o as THREE.Mesh).geometry.dispose();
      });
    }
    this.finishes = [];
  }

  /** A clone with its own toon materials (geometry stays shared with the cached file) and the sku's colors. */
  private clone(gltf: GLTF, asset: RoomItemAsset): THREE.Object3D {
    const object = gltf.scene.clone(true);
    const colors = new Map(asset.textureVariants.map((v) => [v.material, v.color]));
    object.traverse((o) => {
      this.toonify(o);
      for (const m of materialsOf(o)) {
        const color = colors.get(role(m));
        if (color !== undefined) (m as THREE.MeshToonMaterial).color.set(color);
      }
    });
    return object;
  }

  private placeholder(asset: RoomItemAsset, p: Placement): THREE.Object3D {
    const [w, d] = cells(asset, p.surface, 0);
    const size = isWall(p.surface) ? [w * CELL, d * CELL, 0.1] : [w * CELL, 0.5, d * CELL];
    const mesh = new THREE.Mesh(
      new THREE.BoxGeometry(size[0]! - 0.04, size[1]! - 0.04, size[2]! - 0.02),
      this.toon(new THREE.MeshStandardMaterial({ color: 0x8a90a6 })),
    );
    mesh.position.set(0, size[1]! / 2, isWall(p.surface) ? 0.05 : 0);
    mesh.userData["placeholder"] = true;
    const group = new THREE.Group();
    group.add(mesh);
    return group;
  }

  private toonify(object: THREE.Object3D): void {
    const mesh = object as THREE.Mesh;
    if (!mesh.isMesh) return;
    mesh.material = Array.isArray(mesh.material) ? mesh.material.map((m) => this.toon(m)) : this.toon(mesh.material);
  }

  private toon(source: THREE.Material): THREE.MeshToonMaterial {
    const standard = source as THREE.MeshStandardMaterial;
    const material = new THREE.MeshToonMaterial({
      name: role(source),
      color: standard.color?.clone() ?? new THREE.Color(0xffffff),
      emissive: standard.emissive?.clone() ?? new THREE.Color(0x000000),
      side: source.side,
      gradientMap: this.gradient,
    });
    // No normals in the files (flat low-poly look): the toon shader derives face normals (see avatar-renderer).
    return Object.assign(material, { flatShading: true });
  }

  // ------------------------------------------------------------------ input

  private readonly onPointerDown = (event: PointerEvent): void => {
    this.down = { x: event.clientX, y: event.clientY };
  };

  /** A tap or click (not the end of a drag) on the floor or a wall picks that cell. Taps also give a click event. */
  private readonly onClick = (event: MouseEvent): void => {
    const down = this.down;
    this.down = null;
    if (down !== null && Math.hypot(event.clientX - down.x, event.clientY - down.y) > CLICK_SLOP_PX) return;
    const rect = this.canvas.getBoundingClientRect();
    const ndc = new THREE.Vector2(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
    this.raycaster.setFromCamera(ndc, this.camera);
    const hits = this.raycaster.intersectObjects(this.pickPlanes.map((p) => p.mesh), false);
    for (const hit of hits) {
      const surface = this.pickPlanes.find((p) => p.mesh === hit.object)!.surface;
      const pick = cellAt(surface, hit.point);
      if (pick !== null) {
        this.options.onPick(pick);
        return;
      }
    }
  };

  // ------------------------------------------------------------------ frames

  private request(): void {
    if (this.frame !== 0 || this.disposed || this.lost) return;
    this.frame = requestAnimationFrame(this.tick);
  }

  private readonly tick = (): void => {
    this.frame = 0;
    if (this.disposed || this.lost) return;
    this.renderer.render(this.scene, this.camera);
    this.options.onStats({ triangles: this.renderer.info.render.triangles, drawCalls: this.renderer.info.render.calls });
  };

  private fit(): void {
    const width = this.canvas.clientWidth;
    const height = this.canvas.clientHeight;
    if (width === 0 || height === 0) return;
    this.renderer.setSize(width, height, false);
    // The room is about 7 m across in this view; keep it whole at any aspect.
    const view = 3.9;
    const aspect = width / height;
    const halfW = aspect >= 1 ? view * aspect : view;
    const halfH = aspect >= 1 ? view : view / aspect;
    Object.assign(this.camera, { left: -halfW, right: halfW, top: halfH, bottom: -halfH });
    this.camera.updateProjectionMatrix();
    this.request();
  }

  private readonly onLost = (event: Event): void => {
    event.preventDefault();
    if (this.disposed) return;
    this.lost = true;
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.options.onContextLost();
  };

  private readonly onRestored = (): void => {
    if (this.disposed) return;
    this.lost = false;
    this.options.onContextRestored();
    this.request();
  };
}
