import { MOVE_DEAD_ZONE, MOVE_MAX } from "./protocol";

/**
 * Movement input (docs/design/p2-spaces-protocol.md §4): keys and the touch joystick give a direction; the
 * sender turns it into `Move(dx, dz)` messages at the protocol rate. No timers of its own: the caller passes
 * the clock and drives `tick()`, so tests run without fake timers.
 */

export interface Intent {
  dx: number;
  dz: number;
}

export const STOP: Intent = { dx: 0, dz: 0 };
/** Repeat interval while moving, and the minimum time between two Move messages (≤ 10/s). */
export const MOVE_REPEAT_MS = 100;

type Direction = "forward" | "back" | "left" | "right";

const KEYS: Readonly<Record<string, Direction>> = {
  KeyW: "forward",
  ArrowUp: "forward",
  KeyS: "back",
  ArrowDown: "back",
  KeyA: "left",
  ArrowLeft: "left",
  KeyD: "right",
  ArrowRight: "right",
};

/** The direction of a key (`KeyboardEvent.code`), or undefined for other keys. */
export function directionOf(code: string): Direction | undefined {
  return KEYS[code];
}

/**
 * Screen directions to plaza axes. The camera looks along +z (it stands behind the own avatar), so forward is
 * +z and screen right is −x.
 */
export function intentFromScreen(right: number, forward: number): Intent {
  const length = Math.hypot(right, forward);
  if (length < 1e-6) return STOP;
  const scale = Math.min(length, 1) / length;
  const dx = Math.round(-right * scale * MOVE_MAX);
  const dz = Math.round(forward * scale * MOVE_MAX);
  // `+ 0` turns −0 into 0 (MessagePack would send a float for −0).
  return Math.hypot(dx, dz) < MOVE_DEAD_ZONE ? STOP : { dx: dx + 0, dz: dz + 0 };
}

/** Held keys to an intent (full speed; diagonal keeps length 127). */
export function intentFromKeys(held: ReadonlySet<Direction>): Intent {
  const right = (held.has("right") ? 1 : 0) - (held.has("left") ? 1 : 0);
  const forward = (held.has("forward") ? 1 : 0) - (held.has("back") ? 1 : 0);
  return intentFromScreen(right, forward);
}

/**
 * Joystick knob offset (px, screen y down) inside a pad of `radius` px to an intent. Inside the dead zone it is
 * a stop; the length gives the speed, up to full speed at the rim.
 */
export function intentFromJoystick(offsetX: number, offsetY: number, radius: number): Intent {
  if (radius <= 0) return STOP;
  return intentFromScreen(offsetX / radius, -offsetY / radius);
}

export function sameIntent(a: Intent, b: Intent): boolean {
  return a.dx === b.dx && a.dz === b.dz;
}

export function isMoving(intent: Intent): boolean {
  return Math.hypot(intent.dx, intent.dz) >= MOVE_DEAD_ZONE;
}

/**
 * Sends `Move` by the §4 rules: on a change of intent, again every 100 ms while the direction is not zero,
 * and `Move(0, 0)` once on stop. Never more than one message per 100 ms (a changing joystick waits for the
 * next slot and sends the newest intent). A hidden tab sends `Move(0, 0)` and then nothing.
 */
export class MoveSender {
  private intent: Intent = STOP;
  private sentIntent: Intent = STOP;
  private lastSent = Number.NEGATIVE_INFINITY;
  private enabled = true;

  constructor(private readonly send: (dx: number, dz: number) => void) {}

  /** The intent the avatar follows now (also for prediction). */
  get current(): Intent {
    return this.enabled ? this.intent : STOP;
  }

  set(intent: Intent, now: number): void {
    this.intent = isMoving(intent) ? intent : STOP;
    this.tick(now);
  }

  /** Call often (each frame or every few ms). Sends when a message is due. */
  tick(now: number): void {
    if (!this.enabled) return;
    const changed = !sameIntent(this.intent, this.sentIntent);
    const due = now - this.lastSent >= MOVE_REPEAT_MS;
    if (!due) return;
    if (changed || isMoving(this.intent)) this.emit(this.intent, now);
  }

  /**
   * Off (hidden tab, panel that takes the keys, disconnect): a moving avatar gets one `Move(0, 0)` at once,
   * then no input until `enable`. The stop does not wait for the rate slot: it is the last message.
   */
  disable(now: number, connected = true): void {
    if (!this.enabled) return;
    this.enabled = false;
    if (connected && isMoving(this.sentIntent)) this.emit(STOP, now);
    this.intent = STOP;
    this.sentIntent = STOP;
  }

  enable(): void {
    this.enabled = true;
  }

  /** A new connection or instance: the server knows no intent. */
  reset(): void {
    this.intent = STOP;
    this.sentIntent = STOP;
    this.lastSent = Number.NEGATIVE_INFINITY;
  }

  private emit(intent: Intent, now: number): void {
    this.sentIntent = intent;
    this.lastSent = now;
    this.send(intent.dx, intent.dz);
  }
}
