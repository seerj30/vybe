import { gravityAt, type JuggleTouchResult, type StreetJuggleConfig } from "@vybe/game-rules";

/** Height units shown from the ground (bottom) to the top of the play area; a higher ball is drawn at the top. */
export const VIEW_HEIGHT = 130_000;

/** The ball's sideways trip after a kick: drawn only, it never changes the result. */
export interface Flight {
  fromLane: number;
  toLane: number;
  startTick: number;
  ticks: number;
}

/** Ticks until a ball launched upward at `vy` from a tick falls back to its launch height (at least 1). */
export function flightTicks(config: StreetJuggleConfig, vy: number, startTick: number): number {
  const g = gravityAt(config, startTick);
  return Math.max(1, Math.round((2 * vy) / g));
}

/** Lane position of the ball, as a fractional lane, `tick` ticks into the run. */
export function ballLanePosition(flight: Flight | null, lane: number, tick: number): number {
  if (flight === null || flight.toLane !== lane) return lane;
  const f = Math.min(1, Math.max(0, (tick - flight.startTick) / flight.ticks));
  const eased = f * f * (3 - 2 * f);
  return flight.fromLane + (flight.toLane - flight.fromLane) * eased;
}

/** Screen x of a (fractional) lane: the centre of its column. */
export function laneX(lane: number, lanes: number, width: number): number {
  return ((lane + 0.5) / lanes) * width;
}

/** Screen y of a ball height, between `groundY` (height 0) and `topY` (VIEW_HEIGHT). */
export function heightY(y: number, groundY: number, topY: number): number {
  const clamped = Math.min(VIEW_HEIGHT, Math.max(0, y));
  return groundY - (clamped / VIEW_HEIGHT) * (groundY - topY);
}

const RESULT_LABELS: Record<JuggleTouchResult["result"], string> = { perfect: "Perfect", good: "Good", whiff: "Whiff" };

/** The live result label, e.g. "Perfect +175 · streak 3" or "Whiff · streak 0". */
export function touchLabel(touch: JuggleTouchResult): string {
  const points = touch.points > 0 ? ` +${touch.points}` : "";
  return `${RESULT_LABELS[touch.result]}${points} · streak ${touch.streak}`;
}
