import { AIM_X_MAX, AIM_X_MIN, POWER_MAX, POWER_MIN } from "@vybe/game-rules";

function assertFinite(name: string, value: number): void {
  if (!Number.isFinite(value)) throw new RangeError(`${name} must be a finite number, got ${value}`);
}

function clampInt(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.round(value)));
}

/** Integer aimX in [-1000, 1000] (spec §7) from a raw aim value. */
export function quantizeAim(value: number): number {
  assertFinite("aim", value);
  return clampInt(value, AIM_X_MIN, AIM_X_MAX);
}

/** Integer power in [0, 1000] (spec §7) from a raw power value. */
export function quantizePower(value: number): number {
  assertFinite("power", value);
  return clampInt(value, POWER_MIN, POWER_MAX);
}

/** Pointer position across the play area (0 = left edge, 1 = right edge) to aimX. */
export function aimFromFraction(fraction: number): number {
  assertFinite("fraction", fraction);
  return quantizeAim(AIM_X_MIN + fraction * (AIM_X_MAX - AIM_X_MIN));
}

/** Keyboard aim: moves aimX by `unitsPerSecond` in `direction` for `dtMs`. */
export function stepAim(aimX: number, direction: -1 | 0 | 1, dtMs: number, unitsPerSecond: number): number {
  assertFinite("dtMs", dtMs);
  return quantizeAim(aimX + (direction * unitsPerSecond * Math.max(0, dtMs)) / 1000);
}

/**
 * Power while the shot is held: rises from 0 to 1000 in `cycleMs`, falls back
 * to 0 in the next `cycleMs`, and repeats, so an overcharged shot can wait.
 */
export function chargePower(holdMs: number, cycleMs: number): number {
  assertFinite("holdMs", holdMs);
  if (!Number.isFinite(cycleMs) || cycleMs <= 0) throw new RangeError("cycleMs must be positive");
  const phase = Math.max(0, holdMs) % (2 * cycleMs);
  const rising = phase <= cycleMs ? phase : 2 * cycleMs - phase;
  return quantizePower(POWER_MIN + (rising / cycleMs) * (POWER_MAX - POWER_MIN));
}
