import type { JuggleTouch, StreetJuggleConfig } from "@vybe/game-rules";

/** Keyboard spots: left, centre, right. `Space` touches the centre spot. */
const KEY_LANES: Readonly<Record<string, number>> = {
  ArrowLeft: 0,
  KeyA: 0,
  ArrowDown: 1,
  KeyS: 1,
  Space: 1,
  ArrowRight: 2,
  KeyD: 2,
};

/** The spot a key touches, or null when the key is not a game key (or the ruleset has fewer spots). */
export function laneForKey(code: string, lanes: number): number | null {
  const lane = KEY_LANES[code];
  if (lane === undefined) return null;
  if (lanes === 1) return 0;
  return lane < lanes ? lane : null;
}

/** The spot under a pointer: the play area is split into `lanes` equal columns (0 = left edge, 1 = right edge). */
export function laneFromFraction(fraction: number, lanes: number): number {
  if (!Number.isFinite(fraction)) throw new RangeError(`fraction must be finite, got ${fraction}`);
  return Math.min(lanes - 1, Math.max(0, Math.floor(fraction * lanes)));
}

/** Why the client does not record a touch. */
export type TouchGate = "ok" | "time_up" | "max_events" | "cooldown";

/** Mirrors the simulator's range, count and cadence checks, so a recorded trace is never rejected. */
export function touchGate(config: StreetJuggleConfig, events: readonly JuggleTouch[], tMs: number): TouchGate {
  if (tMs < 0 || tMs > config.durationMs - 1) return "time_up";
  if (events.length >= config.maxEvents) return "max_events";
  const last = events[events.length - 1];
  if (last !== undefined && tMs - last.tMs < config.minGapMs) return "cooldown";
  return "ok";
}

/**
 * The touch time for a press at `elapsedMs` after the start. The simulation may already have run the tick
 * of an early press (a frame ran first), so the time moves to the next tick the simulation has not run.
 */
export function touchTime(elapsedMs: number, simTick: number, tickMs: number): number {
  return Math.max(Math.floor(elapsedMs), simTick * tickMs);
}
