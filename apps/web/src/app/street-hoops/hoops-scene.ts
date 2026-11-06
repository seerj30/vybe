import type { ShotResultKind, StreetHoopsConfig } from "@vybe/game-rules";
import * as THREE from "three";
import {
  BALL_RADIUS,
  COURT_HALF_WIDTH,
  FAR_Z,
  NEAR_Z,
  RIM_HEIGHT,
  RIM_RADIUS,
  X_PER_UNIT,
  Z_PER_UNIT,
  type CourtPoint,
} from "./court";

const BALL_START = new THREE.Vector3(0, 1.6, 0.5);
const ARC_HEIGHT = 2.2;
const DROP_MS = 450;
const LINGER_MS = 300;
const RESULT_COLORS: Record<ShotResultKind, number> = { perfect: 0xffc83d, hit: 0x3ddc84, miss: 0xff5a5a };
const RIM_COLOR = 0xff6a1a;
const FLASH_MS = 350;

interface Flight {
  mesh: THREE.Mesh;
  startMs: number;
  flightMs: number;
  end: THREE.Vector3;
  /** Horizontal drift per second after the ball crosses rim height (0 when it goes in). */
  drift: THREE.Vector2;
  result: ShotResultKind;
  reducedMotion: boolean;
  flashed: boolean;
}

export interface FrameState {
  nowMs: number;
  hoop: CourtPoint;
  reticle: CourtPoint | null;
}

/** Street Hoops court. Owns every Three.js object; call dispose() when the view goes away. */
export class HoopsScene {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(50, 1, 0.1, 100);
  private readonly hoop = new THREE.Group();
  private readonly rimMaterial = new THREE.MeshStandardMaterial({ color: RIM_COLOR, roughness: 0.4 });
  private readonly hitZone: THREE.LineLoop;
  private readonly perfectZone: THREE.LineLoop;
  private readonly reticle: THREE.Mesh;
  private readonly ballGeometry = new THREE.SphereGeometry(BALL_RADIUS, 24, 16);
  private readonly ballMaterial = new THREE.MeshStandardMaterial({ color: 0xe8772e, roughness: 0.7 });
  private flights: Flight[] = [];
  private flashUntilMs = 0;

  constructor(canvas: HTMLCanvasElement) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.scene.background = new THREE.Color(0x141826);

    this.scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x303040, 1.6));
    const sun = new THREE.DirectionalLight(0xffffff, 1.4);
    sun.position.set(3, 10, 4);
    this.scene.add(sun);

    const courtDepth = NEAR_Z - FAR_Z + 6;
    const floor = new THREE.Mesh(
      new THREE.PlaneGeometry(COURT_HALF_WIDTH * 2 + 2, courtDepth),
      new THREE.MeshStandardMaterial({ color: 0x2b3350, roughness: 0.9 }),
    );
    floor.rotation.x = -Math.PI / 2;
    floor.position.set(0, 0, (NEAR_Z + FAR_Z) / 2);
    this.scene.add(floor);
    this.scene.add(this.courtLines());

    this.hitZone = this.zoneOutline(0x3ddc84);
    this.perfectZone = this.zoneOutline(0xffc83d);
    this.scene.add(this.hitZone, this.perfectZone);

    this.reticle = new THREE.Mesh(
      new THREE.RingGeometry(0.12, 0.2, 32),
      new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide }),
    );
    this.reticle.rotation.x = -Math.PI / 2;
    this.reticle.position.y = 0.02;
    this.scene.add(this.reticle);

    this.buildHoop();
    this.scene.add(this.hoop);
  }

  /** Sizes the floor zones from the thresholds: the reticle inside a zone at release is that result. */
  setThresholds(config: StreetHoopsConfig): void {
    this.hitZone.scale.set(config.hitDx * X_PER_UNIT, 1, config.hitDp * Z_PER_UNIT);
    this.perfectZone.scale.set(config.perfectDx * X_PER_UNIT, 1, config.perfectDp * Z_PER_UNIT);
  }

  resize(width: number, height: number, pixelRatio: number): void {
    this.renderer.setPixelRatio(Math.min(pixelRatio, 2));
    this.renderer.setSize(width, height, false);
    const aspect = width / height;
    // Portrait screens look down from higher up, so the court depth fills the tall view.
    const portrait = THREE.MathUtils.clamp((1 - aspect) / 0.55, 0, 1);
    this.camera.position.set(0, 5.5 + 4.5 * portrait, 5 - 3 * portrait);
    this.camera.lookAt(0, 1.2 - 0.7 * portrait, -6);
    // Fit the full court width at the near line; the vertical FOV follows the aspect ratio.
    const nearDistance = this.camera.position.distanceTo(new THREE.Vector3(0, 0, NEAR_Z));
    const halfHorizontal = Math.atan((COURT_HALF_WIDTH + 0.6) / nearDistance);
    const vertical = 2 * Math.atan(Math.tan(halfHorizontal) / aspect);
    this.camera.fov = THREE.MathUtils.clamp(THREE.MathUtils.radToDeg(vertical), 40, 100);
    this.camera.aspect = aspect;
    this.camera.updateProjectionMatrix();
  }

  /**
   * Starts a ball toward `end` (rim height), already decided by the simulator.
   * Reduced motion skips the arc: the ball appears at the rim and goes through or off it.
   */
  launch(result: ShotResultKind, end: CourtPoint, hoop: CourtPoint, nowMs: number, flightMs: number, reducedMotion: boolean): void {
    const mesh = new THREE.Mesh(this.ballGeometry, this.ballMaterial);
    const away = new THREE.Vector2(end.x - hoop.x, end.z - hoop.z);
    const drift = result === "miss" ? (away.lengthSq() === 0 ? new THREE.Vector2(1, 0) : away.normalize()).multiplyScalar(1.5) : new THREE.Vector2();
    this.scene.add(mesh);
    this.flights.push({
      mesh,
      startMs: nowMs,
      flightMs: reducedMotion ? 0 : flightMs,
      end: new THREE.Vector3(end.x, RIM_HEIGHT, end.z),
      drift,
      result,
      reducedMotion,
      flashed: false,
    });
  }

  render(state: FrameState): void {
    this.hoop.position.set(state.hoop.x, 0, state.hoop.z);
    this.hitZone.position.set(state.hoop.x, 0.01, state.hoop.z);
    this.perfectZone.position.set(state.hoop.x, 0.015, state.hoop.z);
    this.reticle.visible = state.reticle !== null;
    if (state.reticle !== null) this.reticle.position.set(state.reticle.x, 0.02, state.reticle.z);

    this.flights = this.flights.filter((f) => this.updateFlight(f, state.nowMs));
    if (state.nowMs > this.flashUntilMs) this.rimMaterial.color.setHex(RIM_COLOR);
    this.renderer.render(this.scene, this.camera);
  }

  clearBalls(): void {
    for (const f of this.flights) this.scene.remove(f.mesh);
    this.flights = [];
  }

  dispose(): void {
    this.clearBalls();
    this.scene.traverse((object) => {
      if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
        object.geometry.dispose();
        const materials: THREE.Material[] = Array.isArray(object.material) ? object.material : [object.material];
        for (const m of materials) m.dispose();
      }
    });
    this.ballGeometry.dispose();
    this.ballMaterial.dispose();
    this.renderer.dispose();
  }

  /** Moves one ball; returns false when it is finished and removed. */
  private updateFlight(f: Flight, nowMs: number): boolean {
    const elapsed = nowMs - f.startMs;
    if (elapsed < f.flightMs) {
      const t = elapsed / f.flightMs;
      const position = new THREE.Vector3().lerpVectors(BALL_START, f.end, t);
      position.y += ARC_HEIGHT * 4 * t * (1 - t);
      f.mesh.position.copy(position);
      return true;
    }
    if (!f.flashed) {
      f.flashed = true;
      if (f.result !== "miss") {
        this.rimMaterial.color.setHex(RESULT_COLORS[f.result]);
        this.flashUntilMs = nowMs + FLASH_MS;
      }
    }
    const drop = Math.min(elapsed - f.flightMs, DROP_MS) / 1000;
    const fallen = f.reducedMotion ? 0 : Math.min(RIM_HEIGHT - BALL_RADIUS, 4.9 * drop * drop * 2);
    f.mesh.position.set(f.end.x + f.drift.x * drop, f.end.y - fallen, f.end.z + f.drift.y * drop);
    if (elapsed - f.flightMs < DROP_MS + LINGER_MS) return true;
    this.scene.remove(f.mesh);
    return false;
  }

  private buildHoop(): void {
    const rim = new THREE.Mesh(new THREE.TorusGeometry(RIM_RADIUS, 0.03, 12, 40), this.rimMaterial);
    rim.rotation.x = Math.PI / 2;
    rim.position.y = RIM_HEIGHT;

    const net = new THREE.Mesh(
      new THREE.CylinderGeometry(RIM_RADIUS, RIM_RADIUS * 0.6, 0.45, 16, 3, true),
      new THREE.MeshBasicMaterial({ color: 0xf2f2f2, wireframe: true, transparent: true, opacity: 0.7 }),
    );
    net.position.y = RIM_HEIGHT - 0.225;

    const board = new THREE.Mesh(
      new THREE.BoxGeometry(1.8, 1.05, 0.05),
      new THREE.MeshStandardMaterial({ color: 0xf5f7ff, transparent: true, opacity: 0.85 }),
    );
    board.position.set(0, RIM_HEIGHT + 0.45, -RIM_RADIUS - 0.15);

    const pole = new THREE.Mesh(
      new THREE.CylinderGeometry(0.06, 0.06, RIM_HEIGHT + 0.4, 12),
      new THREE.MeshStandardMaterial({ color: 0x5a6378 }),
    );
    pole.position.set(0, (RIM_HEIGHT + 0.4) / 2, -RIM_RADIUS - 0.35);

    this.hoop.add(rim, net, board, pole);
  }

  /** Unit square outline on the floor; scaled to half-widths in setThresholds(). */
  private zoneOutline(color: number): THREE.LineLoop {
    const geometry = new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(-1, 0, -1),
      new THREE.Vector3(1, 0, -1),
      new THREE.Vector3(1, 0, 1),
      new THREE.Vector3(-1, 0, 1),
    ]);
    return new THREE.LineLoop(geometry, new THREE.LineBasicMaterial({ color }));
  }

  private courtLines(): THREE.LineSegments {
    const w = COURT_HALF_WIDTH;
    const points = [
      [-w, NEAR_Z, -w, FAR_Z],
      [w, NEAR_Z, w, FAR_Z],
      [-w, NEAR_Z, w, NEAR_Z],
      [-w, FAR_Z, w, FAR_Z],
      [0, NEAR_Z, 0, FAR_Z],
    ].flatMap(([x1, z1, x2, z2]) => [new THREE.Vector3(x1, 0.005, z1), new THREE.Vector3(x2, 0.005, z2)]);
    return new THREE.LineSegments(
      new THREE.BufferGeometry().setFromPoints(points),
      new THREE.LineBasicMaterial({ color: 0x5c6b99 }),
    );
  }
}
