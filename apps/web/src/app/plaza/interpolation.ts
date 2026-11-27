import { type AvatarSample, type AvatarState, type Bounds, headingToAngle, TICK_MS } from "./protocol";
import { moveAround, type Obstacle } from "./room-collision";

/**
 * Motion of the avatars between snapshots (docs/design/p2-spaces-protocol.md §5): other avatars render a fixed
 * delay behind the newest server time and interpolate between the two snapshots around that time; the own avatar
 * is predicted from the local input and pulled toward the server position. Pure logic, no Three.js, no timers.
 */

/** Render delay behind the estimated server time (protocol: 100–150 ms). */
export const INTERPOLATION_DELAY_MS = 120;
/** Own avatar: snap to the server position when the error is above this (protocol §5). */
export const SNAP_CM = 100;
/** Snapshots kept per avatar: enough for the delay plus a late frame at 1 Hz idle sends. */
const HISTORY = 12;
/** Arrival offsets kept for the clock estimate (5 s at 10 Hz). */
const OFFSET_WINDOW = 50;

export interface Pose {
  x: number;
  z: number;
  /** Radians around +y; 0 faces +z. */
  angle: number;
  state: AvatarState;
}

interface Timed {
  time: number;
  sample: AvatarSample;
}

/** Shortest-path angle blend. */
export function lerpAngle(a: number, b: number, t: number): number {
  const delta = Math.atan2(Math.sin(b - a), Math.cos(b - a));
  return a + delta * t;
}

/**
 * Maps server ticks to the local clock. The smallest recent (arrival − tick time) is the arrival of a frame
 * without network delay; jitter only adds to it, so the minimum is a stable estimate (it also follows drift).
 */
export class ServerClock {
  private readonly offsets: number[] = [];
  private offset: number | null = null;

  /** Records the arrival of a tick at local time `now` (ms). */
  observe(tick: number, now: number): void {
    this.offsets.push(now - tick * TICK_MS);
    if (this.offsets.length > OFFSET_WINDOW) this.offsets.shift();
    this.offset = Math.min(...this.offsets);
  }

  /** Server time (ms) of a tick. */
  tickTime(tick: number): number {
    return tick * TICK_MS;
  }

  /** Estimated server time now, or null before the first snapshot. */
  serverNow(now: number): number | null {
    return this.offset === null ? null : now - this.offset;
  }

  reset(): void {
    this.offsets.length = 0;
    this.offset = null;
  }
}

/** Snapshot history of the other avatars, sampled at (server time − delay). */
export class Interpolator {
  private readonly history = new Map<number, Timed[]>();
  readonly clock = new ServerClock();
  private lastTick = -1;

  constructor(private readonly delayMs = INTERPOLATION_DELAY_MS) {}

  /**
   * Adds a snapshot received at local time `now`. A snapshot names every avatar of the instance; a slot that is
   * not in it has no position any more. Returns false for an old or repeated tick (dropped).
   */
  push(tick: number, avatars: readonly AvatarSample[], now: number): boolean {
    if (tick <= this.lastTick) return false;
    this.lastTick = tick;
    this.clock.observe(tick, now);
    const time = this.clock.tickTime(tick);
    const present = new Set<number>();
    for (const sample of avatars) {
      present.add(sample.slot);
      let list = this.history.get(sample.slot);
      if (list === undefined) {
        list = [];
        this.history.set(sample.slot, list);
      }
      list.push({ time, sample });
      if (list.length > HISTORY) list.shift();
    }
    for (const slot of [...this.history.keys()]) {
      if (!present.has(slot)) this.history.delete(slot);
    }
    return true;
  }

  has(slot: number): boolean {
    return this.history.has(slot);
  }

  slots(): number[] {
    return [...this.history.keys()];
  }

  /** The newest server sample of a slot (own avatar correction). */
  latest(slot: number): AvatarSample | null {
    const list = this.history.get(slot);
    return list?.[list.length - 1]?.sample ?? null;
  }

  /** The pose of a slot at local time `now`, or null when the slot has no snapshot. */
  sample(slot: number, now: number): Pose | null {
    const list = this.history.get(slot);
    if (list === undefined || list.length === 0) return null;
    const serverNow = this.clock.serverNow(now);
    const target = serverNow === null ? Number.POSITIVE_INFINITY : serverNow - this.delayMs;
    const first = list[0]!;
    if (target <= first.time) return poseOf(first.sample);
    for (let i = list.length - 1; i > 0; i--) {
      const a = list[i - 1]!;
      const b = list[i]!;
      if (target >= a.time && target <= b.time) {
        const t = b.time === a.time ? 1 : (target - a.time) / (b.time - a.time);
        return {
          x: a.sample.x + (b.sample.x - a.sample.x) * t,
          z: a.sample.z + (b.sample.z - a.sample.z) * t,
          angle: lerpAngle(headingToAngle(a.sample.heading), headingToAngle(b.sample.heading), t),
          state: t < 0.5 ? a.sample.state : b.sample.state,
        };
      }
    }
    // Behind the newest snapshot (a late frame): hold the last position, no extrapolation.
    return poseOf(list[list.length - 1]!.sample);
  }

  remove(slot: number): void {
    this.history.delete(slot);
  }

  /** New instance (`moved`) or a new connection: ticks and slots start again. */
  reset(): void {
    this.history.clear();
    this.clock.reset();
    this.lastTick = -1;
  }
}

function poseOf(sample: AvatarSample): Pose {
  return { x: sample.x, z: sample.z, angle: headingToAngle(sample.heading), state: sample.state };
}

/**
 * Own-avatar prediction: §4 movement rule on the local intent, corrected toward the server position. In a room the
 * furniture blocks as on the server (`room-collision.ts`), so the prediction does not walk through it.
 */
export class Predictor {
  x = 0;
  z = 0;
  angle = 0;
  moving = false;
  private started = false;
  /** Predicted positions of the last second: the server position lags behind by the round trip. */
  private readonly path: { time: number; x: number; z: number }[] = [];

  constructor(
    private bounds: Bounds,
    private speed: number,
    private obstacles: readonly Obstacle[] = [],
  ) {}

  configure(bounds: Bounds, speed: number, obstacles: readonly Obstacle[] = []): void {
    this.bounds = bounds;
    this.speed = speed;
    this.obstacles = obstacles;
  }

  get placed(): boolean {
    return this.started;
  }

  /** Puts the avatar at a server position (first snapshot, `moved`, a snap). */
  place(x: number, z: number, angle: number, now: number): void {
    this.x = x;
    this.z = z;
    this.angle = angle;
    this.started = true;
    this.path.length = 0;
    this.path.push({ time: now, x, z });
  }

  /** Advances by `dt` ms with the intent (dx, dz) in −127…127, like the server tick does. */
  step(dx: number, dz: number, dt: number, now: number): void {
    if (!this.started) return;
    const length = Math.hypot(dx, dz);
    this.moving = length >= 16;
    if (this.moving) {
      const scale = Math.min(length / 127, 1);
      const distance = this.speed * scale * (dt / 1000);
      [this.x, this.z] = moveAround(this.x, this.z, (dx / length) * distance, (dz / length) * distance, this.obstacles, this.bounds);
      this.angle = Math.atan2(dx, dz);
    }
    this.path.push({ time: now, x: this.x, z: this.z });
    while (this.path.length > 2 && this.path[0]!.time < now - 1000) this.path.shift();
  }

  /**
   * Corrects toward a server position. The error is the distance to the nearest point of the recent predicted
   * path (the server shows where the client was one round trip ago). Above 100 cm: snap. Else move a part of
   * the error now; repeated each snapshot, this converges without a visible jump. Returns the error in cm.
   */
  correct(sample: AvatarSample, now: number, blend = 0.3): number {
    if (!this.started) {
      this.place(sample.x, sample.z, headingToAngle(sample.heading), now);
      return 0;
    }
    let nearest = { x: this.x, z: this.z };
    let best = Number.POSITIVE_INFINITY;
    for (const point of this.path) {
      const d = Math.hypot(point.x - sample.x, point.z - sample.z);
      if (d < best) {
        best = d;
        nearest = point;
      }
    }
    if (best > SNAP_CM) {
      this.place(sample.x, sample.z, this.moving ? this.angle : headingToAngle(sample.heading), now);
      return best;
    }
    const ex = (sample.x - nearest.x) * blend;
    const ez = (sample.z - nearest.z) * blend;
    // The pull is a move too: it cannot push the avatar into furniture (a snap above can, when the server says so).
    [this.x, this.z] = moveAround(this.x, this.z, ex, ez, this.obstacles, this.bounds);
    for (const point of this.path) {
      point.x += ex;
      point.z += ez;
    }
    // A stopped avatar takes the server heading (the server keeps the last direction of movement).
    if (!this.moving) this.angle = headingToAngle(sample.heading);
    return best;
  }

  reset(): void {
    this.started = false;
    this.moving = false;
    this.path.length = 0;
  }
}
