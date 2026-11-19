import * as THREE from "three";
import { type GLTF, GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { clone as cloneSkinned } from "three/examples/jsm/utils/SkeletonUtils.js";
import { BASE_LAYER_COLOR, BASE_LAYER_REGIONS, type Look } from "../avatar/avatar-look";
import { buildPlazaGround } from "./plaza-ground";
import type { Pose } from "./interpolation";

/**
 * Three.js scene of the plaza (docs/design/p2-spaces-protocol.md, spec §9 budgets). Loaded lazily by the plaza
 * page. Up to 16 avatars share one base file and one item cache. Each avatar is ONE skinned mesh: the visible
 * body regions, face, hair and item meshes are merged into one geometry (all files share the `vybe-rig-1` bind
 * pose), with the body morph baked in and the material colors as vertex colors. So 16 avatars cost 16 draw
 * calls, plus one for the ground and one for the shadows. Each avatar has its own bone copy and mixer.
 */

export interface PlazaStats {
  triangles: number;
  drawCalls: number;
  avatars: number;
}

/** Screen position (CSS px in the canvas) of the point above an avatar's head, for name tags and bubbles. */
export interface TagPosition {
  slot: number;
  x: number;
  y: number;
  visible: boolean;
}

export interface PlazaRendererOptions {
  reducedMotion: boolean;
  idleAnimation: string;
  /** Called first in every frame: the page advances the own-avatar prediction. */
  beforeFrame: (now: number, dtMs: number) => void;
  /** The pose of a slot now (null: not placed yet). */
  pose: (slot: number, now: number) => Pose | null;
  ownSlot: () => number | null;
  onTags: (tags: readonly TagPosition[]) => void;
  onStats: (stats: PlazaStats) => void;
  onContextLost: () => void;
  onContextRestored: () => void;
}

const TOON_LEVELS = new Uint8Array([105, 185, 255]);
const CM = 0.01;
const TAG_HEIGHT = 1.7;
const WALK_SWING = 0.55;
const WALK_RATE = 9;
/** Camera behind and above the own avatar, looking along +z (input.ts maps screen axes the same way). */
const CAMERA_OFFSET = new THREE.Vector3(0, 3.3, -5.6);
const CAMERA_LOOK = new THREE.Vector3(0, 1.0, 1.8);
const WALK_BONES: readonly [string, number][] = [
  ["upper_leg_L", 1],
  ["upper_leg_R", -1],
  ["upper_arm_L", -0.6],
  ["upper_arm_R", 0.6],
];

interface Base {
  gltf: GLTF;
  boneNames: string[];
  boneIndex: Map<string, number>;
  boneInverses: THREE.Matrix4[];
  bindMatrix: THREE.Matrix4;
  idle: THREE.AnimationClip | null;
}

interface Built {
  geometry: THREE.BufferGeometry;
  refs: number;
}

interface WalkBone {
  bone: THREE.Bone;
  axis: THREE.Vector3;
  sign: number;
}

interface AvatarObject {
  slot: number;
  key: string;
  group: THREE.Group;
  mesh: THREE.SkinnedMesh;
  mixer: THREE.AnimationMixer;
  idle: THREE.AnimationAction | null;
  emote: THREE.AnimationAction | null;
  walk: WalkBone[];
  phase: number;
}

function role(material: THREE.Material): string {
  return material.name.split(".")[0] ?? material.name;
}

function lookKey(look: Look): string {
  return JSON.stringify([look.morph, look.skinColor, look.hairColor, look.face, look.presetHair, look.hiddenRegions, look.parts]);
}

export class PlazaRenderer {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(50, 1, 0.1, 120);
  private readonly loader = new GLTFLoader();
  private readonly files = new Map<string, Promise<GLTF>>();
  private readonly built = new Map<string, Promise<Built>>();
  private readonly avatars = new Map<number, AvatarObject>();
  private readonly gradient: THREE.DataTexture;
  private readonly material: THREE.MeshToonMaterial;
  private readonly awayMaterial: THREE.MeshToonMaterial;
  private readonly groundMaterial: THREE.MeshToonMaterial;
  private readonly ground: THREE.Mesh;
  private readonly shadows: THREE.InstancedMesh;
  private readonly timer = new THREE.Timer();
  private readonly resize: ResizeObserver;
  private readonly cameraTarget = new THREE.Vector3();
  private readonly tmp = new THREE.Vector3();
  private readonly tmpQ = new THREE.Quaternion();
  private readonly tmpM = new THREE.Matrix4();
  private base: Promise<Base> | null = null;
  /** Slot → token of the newest `setAvatar` still loading; an older load is dropped when it ends. */
  private readonly pending = new Map<number, number>();
  private nextToken = 1;
  private frame = 0;
  private running = true;
  private disposed = false;
  private lost = false;
  private cameraPlaced = false;
  private width = 1;
  private height = 1;
  private lastStats = "";

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly options: PlazaRendererOptions,
  ) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: "low-power" });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setClearColor(0x9fc6e8);
    this.gradient = new THREE.DataTexture(TOON_LEVELS, TOON_LEVELS.length, 1, THREE.RedFormat);
    this.gradient.minFilter = THREE.NearestFilter;
    this.gradient.magFilter = THREE.NearestFilter;
    this.gradient.generateMipmaps = false;
    this.gradient.needsUpdate = true;
    // The files have no normals (low-poly flat look): FLAT_SHADED takes face normals from screen derivatives.
    const toon = (extra: THREE.MeshToonMaterialParameters = {}) =>
      Object.assign(new THREE.MeshToonMaterial({ vertexColors: true, gradientMap: this.gradient, ...extra }), { flatShading: true });
    this.material = toon();
    this.awayMaterial = toon({ transparent: true, opacity: 0.45, depthWrite: false });
    this.groundMaterial = toon();

    this.scene.fog = new THREE.Fog(0x9fc6e8, 26, 60);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x8a90a6, 1.1));
    const sun = new THREE.DirectionalLight(0xffffff, 2.2);
    sun.position.set(-6, 12, -4);
    this.scene.add(sun);

    this.ground = new THREE.Mesh(buildPlazaGround(), this.groundMaterial);
    this.scene.add(this.ground);

    // Blob shadows: one instanced mesh for all 16 avatars (one draw call).
    const shadowGeometry = new THREE.CircleGeometry(0.42, 16).rotateX(-Math.PI / 2);
    const shadowMaterial = new THREE.MeshBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.22, depthWrite: false });
    this.shadows = new THREE.InstancedMesh(shadowGeometry, shadowMaterial, 16);
    this.shadows.count = 0;
    this.shadows.frustumCulled = false;
    this.shadows.renderOrder = 1;
    this.scene.add(this.shadows);

    this.camera.position.copy(CAMERA_OFFSET);
    this.camera.lookAt(CAMERA_LOOK);

    canvas.addEventListener("webglcontextlost", this.onLost);
    canvas.addEventListener("webglcontextrestored", this.onRestored);
    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(canvas);
    this.fit();
    this.request();
  }

  /** Loads the shared base file. Rejects when it does not load: the page shows the 2D view. */
  async ready(baseUrl: string): Promise<void> {
    await this.loadBase(baseUrl);
  }

  /**
   * Shows (or updates) the avatar of a slot. Returns the skus that did not load (the page retries the look
   * without them: starter items, spec §9 fallback). A newer call for the same slot replaces this one.
   */
  async setAvatar(slot: number, look: Look): Promise<string[]> {
    const key = lookKey(look);
    if (this.avatars.get(slot)?.key === key && !this.pending.has(slot)) return [];
    const token = this.nextToken++;
    this.pending.set(slot, token);
    const failed: string[] = [];
    let base: Base;
    let built: Built;
    try {
      base = await this.loadBase(look.baseUrl);
      built = await this.build(base, look, key, failed);
    } catch {
      if (this.pending.get(slot) === token) this.pending.delete(slot);
      return look.parts.map((p) => p.sku);
    }
    if (this.disposed || this.pending.get(slot) !== token) {
      this.release(key);
      return failed;
    }
    this.pending.delete(slot);
    const old = this.avatars.get(slot);
    if (old !== undefined) this.detach(old);
    this.avatars.set(slot, this.instantiate(slot, base, built.geometry, key));
    this.request();
    return failed;
  }

  removeAvatar(slot: number): void {
    this.pending.delete(slot);
    const avatar = this.avatars.get(slot);
    if (avatar === undefined) return;
    this.detach(avatar);
    this.avatars.delete(slot);
    this.request();
  }

  /** Removes every avatar (new instance after `moved`). */
  clear(): void {
    this.pending.clear();
    for (const slot of [...this.avatars.keys()]) this.removeAvatar(slot);
    this.cameraPlaced = false;
  }

  /** Plays an emote file once on a slot, then the idle loop again. False when it did not load. */
  async playEmote(slot: number, url: string): Promise<boolean> {
    let clip: THREE.AnimationClip | undefined;
    try {
      clip = (await this.load(url)).animations[0];
    } catch {
      return false;
    }
    const avatar = this.avatars.get(slot);
    if (clip === undefined || avatar === undefined || this.disposed) return false;
    avatar.emote?.stop();
    const action = avatar.mixer.clipAction(clip);
    action.setLoop(THREE.LoopOnce, 1);
    action.clampWhenFinished = false;
    action.reset().fadeIn(0.15).play();
    avatar.idle?.fadeOut(0.15);
    avatar.emote = action;
    const done = (event: { action: THREE.AnimationAction }) => {
      if (event.action !== action) return;
      avatar.mixer.removeEventListener("finished", done);
      if (avatar.emote === action) {
        avatar.emote = null;
        if (!this.options.reducedMotion) avatar.idle?.reset().fadeIn(0.25).play();
      }
    };
    avatar.mixer.addEventListener("finished", done);
    return true;
  }

  /** Pauses the frame loop (hidden tab); `true` starts it again. */
  setRunning(running: boolean): void {
    this.running = running;
    if (running) {
      this.timer.reset();
      this.request();
    } else {
      cancelAnimationFrame(this.frame);
      this.frame = 0;
    }
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.resize.disconnect();
    this.canvas.removeEventListener("webglcontextlost", this.onLost);
    this.canvas.removeEventListener("webglcontextrestored", this.onRestored);
    for (const avatar of this.avatars.values()) this.detach(avatar);
    this.avatars.clear();
    for (const built of this.built.values()) void built.then((b) => b.geometry.dispose(), () => undefined);
    for (const file of this.files.values()) {
      void file.then(
        (gltf) => gltf.scene.traverse((object) => (object as THREE.Mesh).geometry?.dispose()),
        () => undefined,
      );
    }
    this.ground.geometry.dispose();
    this.shadows.geometry.dispose();
    (this.shadows.material as THREE.Material).dispose();
    this.shadows.dispose();
    this.material.dispose();
    this.awayMaterial.dispose();
    this.groundMaterial.dispose();
    this.gradient.dispose();
    this.timer.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
  }

  // ------------------------------------------------------------------ loading

  private load(url: string): Promise<GLTF> {
    let file = this.files.get(url);
    if (file === undefined) {
      file = this.loader.loadAsync(url);
      this.files.set(url, file);
      file.catch(() => this.files.delete(url));
    }
    return file;
  }

  private loadBase(url: string): Promise<Base> {
    this.base ??= this.load(url).then(
      (gltf) => this.setupBase(gltf),
      (error: unknown) => {
        this.base = null;
        throw error;
      },
    );
    return this.base;
  }

  private setupBase(gltf: GLTF): Base {
    let skinned: THREE.SkinnedMesh | null = null;
    gltf.scene.traverse((object) => {
      if (skinned === null && (object as THREE.SkinnedMesh).isSkinnedMesh) skinned = object as THREE.SkinnedMesh;
    });
    if (skinned === null) throw new Error("base: no skinned mesh");
    const skeleton = (skinned as THREE.SkinnedMesh).skeleton;
    const boneNames = skeleton.bones.map((b) => b.name);
    return {
      gltf,
      boneNames,
      boneIndex: new Map(boneNames.map((name, i) => [name, i])),
      boneInverses: skeleton.boneInverses,
      bindMatrix: (skinned as THREE.SkinnedMesh).bindMatrix.clone(),
      idle: gltf.animations.find((a) => a.name === this.options.idleAnimation) ?? null,
    };
  }

  /** The merged geometry of a look, shared by every avatar with the same look (reference counted). */
  private build(base: Base, look: Look, key: string, failed: string[]): Promise<Built> {
    let built = this.built.get(key);
    if (built === undefined) {
      built = this.merge(base, look, failed).then((geometry) => ({ geometry, refs: 0 }));
      this.built.set(key, built);
      built.catch(() => this.built.delete(key));
    }
    return built.then((b) => {
      b.refs++;
      return b;
    });
  }

  private release(key: string): void {
    const built = this.built.get(key);
    if (built === undefined) return;
    void built.then((b) => {
      b.refs--;
      if (b.refs > 0 || this.built.get(key) !== built) return;
      this.built.delete(key);
      b.geometry.dispose();
    });
  }

  private async merge(base: Base, look: Look, failed: string[]): Promise<THREE.BufferGeometry> {
    const skin = new THREE.Color(look.skinColor);
    const hair = new THREE.Color(look.hairColor);
    const layer = new THREE.Color(BASE_LAYER_COLOR);
    const hidden = new Set(look.hiddenRegions);
    const sources: { mesh: THREE.SkinnedMesh; color: (m: THREE.Material) => THREE.Color }[] = [];

    base.gltf.scene.traverse((object) => {
      const mesh = object as THREE.SkinnedMesh;
      if (!mesh.isSkinnedMesh) return;
      const owner = mesh.parent?.name === "vybe_rig" ? mesh : (mesh.parent ?? mesh);
      const name = owner.name;
      let include = false;
      let region: string | null = null;
      if (name.startsWith("region_")) {
        region = name.slice("region_".length);
        include = !hidden.has(region);
      } else if (/^face_\d+$/.test(name)) include = name === look.face;
      else if (/^hair_[a-z]+$/.test(name)) include = name === look.presetHair;
      if (!include) return;
      const regionName = region;
      sources.push({
        mesh,
        color: (m) => {
          const r = role(m);
          if (r === "skin") return regionName !== null && BASE_LAYER_REGIONS.includes(regionName) ? layer : skin;
          if (r === "hair") return hair;
          return baseColor(m);
        },
      });
    });

    const files = await Promise.allSettled(look.parts.map((part) => this.load(part.url)));
    look.parts.forEach((part, i) => {
      const result = files[i]!;
      if (result.status === "rejected") {
        failed.push(part.sku);
        return;
      }
      const root = part.node === null ? result.value.scene : result.value.scene.getObjectByName(part.node);
      const meshes: THREE.SkinnedMesh[] = [];
      root?.traverse((object) => {
        if ((object as THREE.SkinnedMesh).isSkinnedMesh) meshes.push(object as THREE.SkinnedMesh);
      });
      // A file that does not match the rig is left off (the page shows the starter item instead).
      if (meshes.length === 0 || meshes.some((m) => m.skeleton.bones.some((b) => !base.boneIndex.has(b.name)))) {
        failed.push(part.sku);
        return;
      }
      for (const mesh of meshes) {
        sources.push({
          mesh,
          color: (m) => {
            const r = role(m);
            if (r === "skin") return skin;
            if (r === "hair") return hair;
            const own = part.colors[m.name];
            return own === undefined ? baseColor(m) : new THREE.Color(own);
          },
        });
      }
    });
    return mergeSkinned(sources, base.boneIndex, look.morph);
  }

  private instantiate(slot: number, base: Base, geometry: THREE.BufferGeometry, key: string): AvatarObject {
    const root = cloneSkinned(base.gltf.scene);
    // Only the bones of the copy are used; its meshes are replaced by the merged one.
    const drop: THREE.Object3D[] = [];
    const bones = new Map<string, THREE.Bone>();
    root.traverse((object) => {
      if ((object as THREE.Mesh).isMesh) drop.push(object);
      if ((object as THREE.Bone).isBone) bones.set(object.name, object as THREE.Bone);
    });
    for (const object of drop) object.removeFromParent();
    const mesh = new THREE.SkinnedMesh(geometry, this.material);
    mesh.frustumCulled = false;
    const rig = root.getObjectByName("vybe_rig") ?? root;
    rig.add(mesh);
    mesh.bind(new THREE.Skeleton(base.boneNames.map((name) => bones.get(name)!), base.boneInverses), base.bindMatrix);

    const group = new THREE.Group();
    group.add(root);
    group.visible = false;
    this.scene.add(group);

    const mixer = new THREE.AnimationMixer(root);
    const idle = base.idle === null ? null : mixer.clipAction(base.idle);
    if (idle !== null && !this.options.reducedMotion) {
      idle.play();
      idle.time = (slot * 0.37) % Math.max(base.idle!.duration, 0.01); // not everyone breathes in step
    }
    root.updateMatrixWorld(true);
    const walk: WalkBone[] = [];
    for (const [name, sign] of WALK_BONES) {
      const bone = bones.get(name);
      if (bone === undefined || bone.parent === null) continue;
      // The swing axis is the avatar's x axis, expressed in the bone's parent space.
      const parentWorld = bone.parent.getWorldQuaternion(new THREE.Quaternion());
      const rootWorld = root.getWorldQuaternion(new THREE.Quaternion());
      const axis = new THREE.Vector3(1, 0, 0).applyQuaternion(rootWorld).applyQuaternion(parentWorld.invert());
      walk.push({ bone, axis, sign });
    }
    return { slot, key, group, mesh, mixer, idle, emote: null, walk, phase: 0 };
  }

  private detach(avatar: AvatarObject): void {
    avatar.mixer.stopAllAction();
    avatar.mixer.uncacheRoot(avatar.mixer.getRoot());
    avatar.group.removeFromParent();
    avatar.mesh.skeleton.dispose();
    this.release(avatar.key);
  }

  // ------------------------------------------------------------------ frames

  private request(): void {
    if (this.frame !== 0 || this.disposed || this.lost || !this.running) return;
    this.frame = requestAnimationFrame(this.tick);
  }

  private readonly tick = (): void => {
    this.frame = 0;
    if (this.disposed || this.lost || !this.running) return;
    this.timer.update();
    const dt = Math.min(this.timer.getDelta(), 0.1);
    const now = performance.now();
    this.options.beforeFrame(now, dt * 1000);

    const own = this.options.ownSlot();
    const tags: TagPosition[] = [];
    let shadows = 0;
    for (const avatar of this.avatars.values()) {
      const pose = this.options.pose(avatar.slot, now);
      avatar.group.visible = pose !== null;
      if (pose === null) {
        tags.push({ slot: avatar.slot, x: 0, y: 0, visible: false });
        continue;
      }
      avatar.group.position.set(pose.x * CM, 0, pose.z * CM);
      avatar.group.rotation.y = pose.angle;
      avatar.mesh.material = pose.state === 2 ? this.awayMaterial : this.material;
      avatar.mixer.update(dt);
      this.walk(avatar, pose.state === 1 && avatar.emote === null, dt);
      this.tmpM.makeTranslation(pose.x * CM, 0.01, pose.z * CM);
      this.shadows.setMatrixAt(shadows++, this.tmpM);
      if (avatar.slot === own) this.follow(avatar.group.position, dt);
      tags.push(this.project(avatar.slot, avatar.group.position));
    }
    this.shadows.count = shadows;
    this.shadows.instanceMatrix.needsUpdate = true;
    this.renderer.render(this.scene, this.camera);
    this.options.onTags(tags);
    const info = this.renderer.info.render;
    const stats = `${info.triangles}|${info.calls}|${this.avatars.size}`;
    if (stats !== this.lastStats) {
      this.lastStats = stats;
      this.options.onStats({ triangles: info.triangles, drawCalls: info.calls, avatars: this.avatars.size });
    }
    this.request();
  };

  /** A simple walk on top of the idle: legs and arms swing around the avatar's x axis. */
  private walk(avatar: AvatarObject, walking: boolean, dt: number): void {
    if (this.options.reducedMotion || !walking || avatar.idle === null) {
      avatar.phase = 0;
      return;
    }
    avatar.phase += dt * WALK_RATE;
    const swing = Math.sin(avatar.phase) * WALK_SWING;
    for (const { bone, axis, sign } of avatar.walk) {
      this.tmpQ.setFromAxisAngle(axis, swing * sign);
      bone.quaternion.premultiply(this.tmpQ);
    }
  }

  private follow(target: THREE.Vector3, dt: number): void {
    const portrait = this.width < this.height;
    const scale = portrait ? 1.55 : 1;
    if (!this.cameraPlaced || this.options.reducedMotion) {
      this.cameraTarget.copy(target);
      this.cameraPlaced = true;
    } else {
      this.cameraTarget.lerp(target, 1 - Math.exp(-dt * 6));
    }
    this.camera.position.copy(this.cameraTarget).addScaledVector(CAMERA_OFFSET, scale);
    this.tmp.copy(this.cameraTarget).add(CAMERA_LOOK);
    this.camera.lookAt(this.tmp);
  }

  private project(slot: number, position: THREE.Vector3): TagPosition {
    this.tmp.set(position.x, TAG_HEIGHT, position.z).project(this.camera);
    const visible = this.tmp.z > -1 && this.tmp.z < 1 && Math.abs(this.tmp.x) < 1.05 && Math.abs(this.tmp.y) < 1.05;
    return { slot, x: ((this.tmp.x + 1) / 2) * this.width, y: ((1 - this.tmp.y) / 2) * this.height, visible };
  }

  private fit(): void {
    const width = this.canvas.clientWidth;
    const height = this.canvas.clientHeight;
    if (width === 0 || height === 0) return;
    this.width = width;
    this.height = height;
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.camera.fov = width < height ? 60 : 50;
    this.camera.updateProjectionMatrix();
    this.request();
  }

  private readonly onLost = (event: Event): void => {
    event.preventDefault(); // allows the browser to restore the context
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

function baseColor(material: THREE.Material): THREE.Color {
  const m = material as THREE.MeshStandardMaterial;
  const color = m.color?.clone() ?? new THREE.Color(0xffffff);
  // Glow parts: the emissive color lifts the vertex color (one shared material has no emissive per part).
  if (m.emissive !== undefined) color.add(m.emissive.clone().multiplyScalar(m.emissiveIntensity ?? 1));
  return color.setRGB(Math.min(color.r, 1), Math.min(color.g, 1), Math.min(color.b, 1));
}

/**
 * Merges skinned meshes that share one bind pose into one geometry: positions with the body morph baked in,
 * joints remapped to the base skeleton order, weights, and the material color per vertex.
 */
export function mergeSkinned(
  sources: readonly { mesh: THREE.SkinnedMesh; color: (m: THREE.Material) => THREE.Color }[],
  boneIndex: ReadonlyMap<string, number>,
  morph: number,
): THREE.BufferGeometry {
  let vertices = 0;
  let indices = 0;
  for (const { mesh } of sources) {
    vertices += mesh.geometry.getAttribute("position").count;
    indices += mesh.geometry.index?.count ?? mesh.geometry.getAttribute("position").count;
  }
  const position = new Float32Array(vertices * 3);
  const skinIndex = new Uint16Array(vertices * 4);
  const skinWeight = new Float32Array(vertices * 4);
  const color = new Float32Array(vertices * 3);
  const index = vertices > 65535 ? new Uint32Array(indices) : new Uint16Array(indices);
  let v = 0;
  let k = 0;
  for (const { mesh, color: colorOf } of sources) {
    const geometry = mesh.geometry;
    const pos = geometry.getAttribute("position");
    const joints = geometry.getAttribute("skinIndex");
    const weights = geometry.getAttribute("skinWeight");
    const morphIndex = mesh.morphTargetDictionary?.["body_b"];
    const delta = morphIndex === undefined ? undefined : geometry.morphAttributes["position"]?.[morphIndex];
    const relative = geometry.morphTargetsRelative;
    const remap = mesh.skeleton.bones.map((b) => boneIndex.get(b.name) ?? 0);
    const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    const groups = geometry.groups.length > 0 ? geometry.groups : [{ start: 0, count: Number.POSITIVE_INFINITY, materialIndex: 0 }];
    const vertexColor = new Array<THREE.Color>(pos.count);
    const order = geometry.index;
    for (const group of groups) {
      const c = colorOf(materials[group.materialIndex ?? 0] ?? materials[0]!);
      const end = Math.min(group.start + group.count, order?.count ?? pos.count);
      for (let i = group.start; i < end; i++) vertexColor[order === null ? i : order.getX(i)] = c;
    }
    for (let i = 0; i < pos.count; i++) {
      let x = pos.getX(i);
      let y = pos.getY(i);
      let z = pos.getZ(i);
      if (delta !== undefined && morph !== 0) {
        const dx = delta.getX(i);
        const dy = delta.getY(i);
        const dz = delta.getZ(i);
        x = relative ? x + dx * morph : x + (dx - x) * morph;
        y = relative ? y + dy * morph : y + (dy - y) * morph;
        z = relative ? z + dz * morph : z + (dz - z) * morph;
      }
      const o = (v + i) * 3;
      position[o] = x;
      position[o + 1] = y;
      position[o + 2] = z;
      const c = vertexColor[i] ?? colorOf(materials[0]!);
      color[o] = c.r;
      color[o + 1] = c.g;
      color[o + 2] = c.b;
      for (let j = 0; j < 4; j++) {
        skinIndex[(v + i) * 4 + j] = remap[joints.getComponent(i, j)] ?? 0;
        skinWeight[(v + i) * 4 + j] = weights.getComponent(i, j);
      }
    }
    if (order === null) {
      for (let i = 0; i < pos.count; i++) index[k++] = v + i;
    } else {
      for (let i = 0; i < order.count; i++) index[k++] = v + order.getX(i);
    }
    v += pos.count;
  }
  const merged = new THREE.BufferGeometry();
  merged.setAttribute("position", new THREE.BufferAttribute(position, 3));
  merged.setAttribute("skinIndex", new THREE.BufferAttribute(skinIndex, 4));
  merged.setAttribute("skinWeight", new THREE.BufferAttribute(skinWeight, 4));
  merged.setAttribute("color", new THREE.BufferAttribute(color, 3));
  merged.setIndex(new THREE.BufferAttribute(index, 1));
  merged.computeBoundingSphere();
  return merged;
}
