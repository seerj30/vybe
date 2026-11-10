import * as THREE from "three";
import { type GLTF, GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { BASE_LAYER_COLOR, BASE_LAYER_REGIONS, type Look, type LookPart } from "../avatar/avatar-look";

/**
 * Three.js scene of the avatar (spec §9, §10). Loaded lazily by AvatarViewport; owns every GPU object and
 * frees them in dispose(). Item files share the base rig: their skinned meshes are bound to the base skeleton
 * by bone name, so one skeleton drives the body, the garments and the emotes.
 */

export interface ShowReport {
  /** True when the base avatar file did not load: the caller shows the 2D portrait. */
  baseFailed: boolean;
  /** Skus whose file did not load (or does not match the rig); they are not shown. */
  failed: string[];
  /** False when a newer show() call replaced this one before it finished. */
  current: boolean;
}

/** done: played to the end; stopped: replaced or disposed; failed: the file did not load. */
export type EmoteResult = "done" | "stopped" | "failed";

export interface RenderStats {
  triangles: number;
  drawCalls: number;
}

export interface RendererOptions {
  reducedMotion: boolean;
  idleAnimation: string;
  onStats: (stats: RenderStats) => void;
  onContextLost: () => void;
  onContextRestored: () => void;
}

interface Base {
  gltf: GLTF;
  rig: THREE.Object3D;
  bones: Map<string, THREE.Bone>;
  mixer: THREE.AnimationMixer;
  idle: THREE.AnimationAction | null;
  regions: Map<string, THREE.Object3D>;
}

interface ShownPart {
  id: string;
  objects: THREE.SkinnedMesh[];
}

const TOON_LEVELS = new Uint8Array([105, 185, 255]);
const SKIN = "skin";
const HAIR = "hair";
const TURN_LIMIT = Math.PI;

function role(material: THREE.Material): string {
  return material.name.split(".")[0] ?? material.name;
}

function partId(part: LookPart): string {
  return `${part.key}|${part.url}|${part.node ?? ""}|${JSON.stringify(part.colors)}`;
}

function materialsOf(object: THREE.Object3D): THREE.Material[] {
  const mesh = object as THREE.Mesh;
  if (!mesh.isMesh) return [];
  return Array.isArray(mesh.material) ? mesh.material : [mesh.material];
}

export class AvatarRenderer {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(26, 3 / 4, 0.1, 20);
  private readonly turntable = new THREE.Group();
  private readonly loader = new GLTFLoader();
  private readonly files = new Map<string, Promise<GLTF>>();
  private readonly gradient: THREE.DataTexture;
  private readonly shown = new Map<string, ShownPart>();
  private readonly timer = new THREE.Timer();
  private readonly resize: ResizeObserver;
  private base: Base | null = null;
  private baseLoad: Promise<Base> | null = null;
  private emote: THREE.AnimationAction | null = null;
  private emoteEnd: ((completed: boolean) => void) | null = null;
  private token = 0;
  private frame = 0;
  private disposed = false;
  private lost = false;
  private yaw = -0.35;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly options: RendererOptions,
  ) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "low-power" });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.gradient = new THREE.DataTexture(TOON_LEVELS, TOON_LEVELS.length, 1, THREE.RedFormat);
    this.gradient.minFilter = THREE.NearestFilter;
    this.gradient.magFilter = THREE.NearestFilter;
    this.gradient.generateMipmaps = false;
    this.gradient.needsUpdate = true;

    this.scene.add(new THREE.AmbientLight(0xffffff, 1.1));
    const key = new THREE.DirectionalLight(0xffffff, 2.4);
    key.position.set(-1.6, 2.4, 3);
    this.scene.add(key);
    this.scene.add(this.turntable);
    this.turntable.rotation.y = this.yaw;
    this.camera.position.set(0, 1.0, 3.85);
    this.camera.lookAt(0, 0.8, 0);

    canvas.addEventListener("webglcontextlost", this.onLost);
    canvas.addEventListener("webglcontextrestored", this.onRestored);
    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(canvas);
    this.fit();
  }

  /** Shows a look: loads the files it needs, binds them to the rig, applies colors and coverage masks. */
  async show(look: Look): Promise<ShowReport> {
    const token = ++this.token;
    let base: Base;
    try {
      base = await this.loadBase(look.baseUrl);
    } catch {
      return { baseFailed: true, failed: [], current: token === this.token };
    }
    const wanted = look.parts.filter((part) => !this.shown.has(partId(part)));
    const loaded = await Promise.allSettled(wanted.map((part) => this.load(part.url)));
    if (token !== this.token || this.disposed) return { baseFailed: false, failed: [], current: false };

    const failed: string[] = [];
    const keep = new Set(look.parts.map(partId));
    for (const [id, part] of this.shown) {
      if (!keep.has(id)) this.remove(part);
    }
    wanted.forEach((part, i) => {
      const result = loaded[i]!;
      if (result.status === "rejected") {
        failed.push(part.sku);
        return;
      }
      try {
        this.shown.set(partId(part), { id: partId(part), objects: this.attach(base, result.value, part) });
      } catch {
        failed.push(part.sku);
      }
    });
    this.apply(base, look);
    this.request();
    return { baseFailed: false, failed, current: true };
  }

  /** Plays an emote file once, then returns to the idle loop (or the rest pose with reduced motion). */
  async playEmote(url: string): Promise<EmoteResult> {
    if (this.base === null) return "failed";
    let clip: THREE.AnimationClip | undefined;
    try {
      clip = (await this.load(url)).animations[0];
    } catch {
      return "failed";
    }
    const base = this.base;
    if (clip === undefined) return "failed";
    if (this.disposed || base !== this.base) return "stopped";
    this.settleEmote(false);
    const action = base.mixer.clipAction(clip);
    action.setLoop(THREE.LoopOnce, 1);
    action.reset().fadeIn(0.15).play();
    base.idle?.fadeOut(0.15);
    this.emote = action;
    this.request();
    return new Promise((resolve) => {
      const done = (event: { action: THREE.AnimationAction }) => {
        if (event.action === action) this.settleEmote(true);
      };
      this.emoteEnd = (completed: boolean) => {
        base.mixer.removeEventListener("finished", done);
        action.stop();
        if (completed && !this.options.reducedMotion) base.idle?.reset().fadeIn(0.25).play();
        resolve(completed ? "done" : "stopped");
      };
      base.mixer.addEventListener("finished", done);
    });
  }

  /** Ends the running emote: completed (back to idle) or replaced/disposed (the caller learns it did not finish). */
  private settleEmote(completed: boolean): void {
    const end = this.emoteEnd;
    this.emoteEnd = null;
    this.emote = null;
    end?.(completed);
    this.request();
  }

  /** Turns the avatar around its vertical axis (radians, positive = to the right). */
  turn(delta: number): void {
    this.yaw = THREE.MathUtils.euclideanModulo(this.yaw + delta + TURN_LIMIT, 2 * TURN_LIMIT) - TURN_LIMIT;
    this.turntable.rotation.y = this.yaw;
    this.request();
  }

  dispose(): void {
    if (this.disposed) return;
    this.settleEmote(false);
    this.disposed = true;
    this.token++;
    cancelAnimationFrame(this.frame);
    this.resize.disconnect();
    this.canvas.removeEventListener("webglcontextlost", this.onLost);
    this.canvas.removeEventListener("webglcontextrestored", this.onRestored);
    this.base?.mixer.stopAllAction();
    this.turntable.traverse((object) => {
      for (const m of materialsOf(object)) m.dispose();
    });
    for (const file of this.files.values()) {
      void file.then(
        (gltf) => gltf.scene.traverse((object) => (object as THREE.Mesh).geometry?.dispose()),
        () => undefined,
      );
    }
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
      // A failed file may load on a later try (spec §9: fallback now, the real item next time).
      file.catch(() => this.files.delete(url));
    }
    return file;
  }

  private loadBase(url: string): Promise<Base> {
    if (this.base !== null) return Promise.resolve(this.base);
    this.baseLoad ??= this.load(url).then(
      (gltf) => this.setupBase(gltf),
      (error: unknown) => {
        this.baseLoad = null;
        throw error;
      },
    );
    return this.baseLoad;
  }

  private setupBase(gltf: GLTF): Base {
    const bones = new Map<string, THREE.Bone>();
    const regions = new Map<string, THREE.Object3D>();
    let rig: THREE.Object3D | null = null;
    gltf.scene.traverse((object) => {
      if ((object as THREE.Bone).isBone) bones.set(object.name, object as THREE.Bone);
      if (object.name.startsWith("region_")) regions.set(object.name.slice("region_".length), object);
      if (object.name === "vybe_rig") rig = object;
      const mesh = object as THREE.Mesh;
      if (mesh.isMesh) {
        // Every body region gets its own materials, so the base layer can color some of them.
        mesh.material = Array.isArray(mesh.material) ? mesh.material.map((m) => this.toon(m)) : this.toon(mesh.material);
        mesh.frustumCulled = false;
      }
    });
    const mixer = new THREE.AnimationMixer(gltf.scene);
    const clip = gltf.animations.find((a) => a.name === this.options.idleAnimation);
    const idle = clip === undefined ? null : mixer.clipAction(clip);
    if (idle !== null && !this.options.reducedMotion) idle.play();
    this.turntable.add(gltf.scene);
    this.base = { gltf, rig: rig ?? gltf.scene, bones, mixer, idle, regions };
    return this.base;
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
    // The files have no normals (low-poly flat look): the shader takes face normals from screen-space
    // derivatives. The toon shader supports FLAT_SHADED; only the type declaration lacks the property.
    return Object.assign(material, { flatShading: true });
  }

  /** Clones the part's skinned meshes and binds them to the base skeleton (same rig, matched by bone name). */
  private attach(base: Base, gltf: GLTF, part: LookPart): THREE.SkinnedMesh[] {
    const root = part.node === null ? gltf.scene : gltf.scene.getObjectByName(part.node);
    if (root === undefined) throw new Error(`${part.sku}: no mesh ${part.node}`);
    const sources: THREE.SkinnedMesh[] = [];
    root.traverse((object) => {
      if ((object as THREE.SkinnedMesh).isSkinnedMesh) sources.push(object as THREE.SkinnedMesh);
    });
    if (sources.length === 0) throw new Error(`${part.sku}: no skinned mesh`);
    // Check every bone first, so a rig mismatch never leaves half an item in the scene.
    const skeletons = sources.map((source) =>
      source.skeleton.bones.map((bone) => {
        const target = base.bones.get(bone.name);
        if (target === undefined) throw new Error(`${part.sku}: bone ${bone.name} is not in the rig`);
        return target;
      }),
    );
    return sources.map((source, i) => {
      const bones = skeletons[i]!;
      const mesh = new THREE.SkinnedMesh(source.geometry, Array.isArray(source.material) ? source.material.map((m) => this.toon(m)) : this.toon(source.material));
      mesh.name = source.name;
      mesh.frustumCulled = false;
      mesh.morphTargetInfluences = source.morphTargetInfluences?.slice();
      mesh.morphTargetDictionary = source.morphTargetDictionary;
      base.rig.add(mesh);
      mesh.bind(new THREE.Skeleton(bones, source.skeleton.boneInverses), source.bindMatrix);
      for (const m of materialsOf(mesh)) {
        const color = part.colors[m.name];
        if (color !== undefined) (m as THREE.MeshToonMaterial).color.set(color);
      }
      return mesh;
    });
  }

  private remove(part: ShownPart): void {
    for (const mesh of part.objects) {
      mesh.removeFromParent();
      mesh.skeleton.dispose();
      // Shared with the cached file: disposing frees the GPU buffers; three uploads them again if reused.
      mesh.geometry.dispose();
      for (const m of materialsOf(mesh)) m.dispose();
    }
    this.shown.delete(part.id);
  }

  // ------------------------------------------------------------------ look

  private apply(base: Base, look: Look): void {
    const hidden = new Set(look.hiddenRegions);
    for (const [name, object] of base.regions) object.visible = !hidden.has(name);
    base.gltf.scene.traverse((object) => {
      if (/^face_\d+$/.test(object.name)) object.visible = object.name === look.face;
      if (/^hair_[a-z]+$/.test(object.name) && object.parent?.name === "vybe_rig") object.visible = object.name === look.presetHair;
    });
    const skin = new THREE.Color(look.skinColor);
    const hair = new THREE.Color(look.hairColor);
    const layer = new THREE.Color(BASE_LAYER_COLOR);
    for (const [name, object] of base.regions) {
      for (const m of materialsOf(object)) {
        if (role(m) === SKIN) (m as THREE.MeshToonMaterial).color.copy(BASE_LAYER_REGIONS.includes(name) ? layer : skin);
      }
    }
    const regionMeshes = new Set(base.regions.values());
    this.turntable.traverse((object) => {
      if (!regionMeshes.has(object)) {
        // Preset hair, faces and items: the player's skin tone (slides) and hair color (hair items).
        for (const m of materialsOf(object)) {
          if (role(m) === SKIN) (m as THREE.MeshToonMaterial).color.copy(skin);
          if (role(m) === HAIR) (m as THREE.MeshToonMaterial).color.copy(hair);
        }
      }
      const mesh = object as THREE.Mesh;
      const index = mesh.morphTargetDictionary?.["body_b"];
      if (index !== undefined && mesh.morphTargetInfluences) mesh.morphTargetInfluences[index] = look.morph;
    });
  }

  // ------------------------------------------------------------------ frames

  private animating(): boolean {
    return this.emote !== null || (this.base?.idle?.isRunning() ?? false);
  }

  private request(): void {
    if (this.frame !== 0 || this.disposed || this.lost) return;
    this.frame = requestAnimationFrame(this.tick);
  }

  private readonly tick = (): void => {
    this.frame = 0;
    if (this.disposed || this.lost) return;
    this.timer.update();
    this.base?.mixer.update(Math.min(this.timer.getDelta(), 0.1));
    this.renderer.render(this.scene, this.camera);
    this.options.onStats({ triangles: this.renderer.info.render.triangles, drawCalls: this.renderer.info.render.calls });
    if (this.animating()) this.request();
  };

  private fit(): void {
    const width = this.canvas.clientWidth;
    const height = this.canvas.clientHeight;
    if (width === 0 || height === 0) return;
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
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
