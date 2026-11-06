import { Pcg32 } from "./pcg32.ts";
import {
  AIM_X_MAX,
  AIM_X_MIN,
  POWER_MAX,
  POWER_MIN,
  type EvaluateInput,
  type EvaluateResult,
  type RejectionReason,
  type ShotEvent,
  type ShotResult,
  type StreetHoopsConfig,
  type Waypoint,
} from "./types.ts";

const UINT32_MAX = 0xffffffff;

/** Validates the config invariants the simulator depends on. */
export function assertValidConfig(config: StreetHoopsConfig): void {
  for (const [key, value] of Object.entries(config)) {
    if (!Number.isSafeInteger(value)) {
      throw new RangeError(`config.${key} must be an integer`);
    }
  }
  if (config.streamId < 0 || config.streamId > UINT32_MAX) {
    throw new RangeError("config.streamId must be uint32");
  }
  if (config.segmentMs <= 0 || config.durationMs <= 0 || config.durationMs % config.segmentMs !== 0) {
    throw new RangeError("config.durationMs must be a positive multiple of config.segmentMs");
  }
  if (config.xMin > config.xMax || config.pMin > config.pMax) {
    throw new RangeError("config waypoint ranges are empty");
  }
  if (config.minGapMs <= 0 || config.maxEvents < 0) {
    throw new RangeError("config.minGapMs must be positive and config.maxEvents non-negative");
  }
}

/** The API's invalid_event check (§7 step 4) must run first; a non-integer here is a caller bug. */
function assertIntegerEvents(events: readonly ShotEvent[]): void {
  for (const e of events) {
    if (!Number.isSafeInteger(e.tMs) || !Number.isSafeInteger(e.aimX) || !Number.isSafeInteger(e.power)) {
      throw new TypeError("events must contain only integers; run the invalid_event check first");
    }
  }
}

export function waypointCount(config: StreetHoopsConfig): number {
  return config.durationMs / config.segmentMs + 1;
}

/** Draws the basket waypoints interleaved: x_0, p_0, x_1, p_1, … (§7). */
export function generateWaypoints(config: StreetHoopsConfig, seed: number): Waypoint[] {
  assertValidConfig(config);
  if (!Number.isInteger(seed) || seed < 0 || seed > UINT32_MAX) {
    throw new RangeError("seed must be uint32");
  }
  const rng = new Pcg32(BigInt(seed), BigInt(config.streamId));
  const waypoints: Waypoint[] = [];
  for (let k = 0; k < waypointCount(config); k++) {
    const x = rng.nextInRange(config.xMin, config.xMax);
    const p = rng.nextInRange(config.pMin, config.pMax);
    waypoints.push({ x, p });
  }
  return waypoints;
}

function interpolate(from: number, to: number, f: number, segmentMs: number): number {
  // Integer division rounding toward zero (C# `/` on int).
  return from + Math.trunc(((to - from) * f) / segmentMs);
}

/** Target position and ideal power at time t in [0, durationMs - 1]. */
export function targetAt(
  config: StreetHoopsConfig,
  waypoints: readonly Waypoint[],
  tMs: number,
): { targetX: number; idealPower: number } {
  if (!Number.isInteger(tMs) || tMs < 0 || tMs >= config.durationMs) {
    throw new RangeError(`tMs ${tMs} outside [0, ${config.durationMs - 1}]`);
  }
  const k = Math.floor(tMs / config.segmentMs);
  const f = tMs % config.segmentMs;
  const a = waypoints[k];
  const b = waypoints[k + 1];
  if (a === undefined || b === undefined) {
    throw new RangeError(`missing waypoint for segment ${k}`);
  }
  return {
    targetX: interpolate(a.x, b.x, f, config.segmentMs),
    idealPower: interpolate(a.p, b.p, f, config.segmentMs),
  };
}

export function streakBonus(config: StreetHoopsConfig, streak: number): number {
  if (streak < config.streakStart) return 0;
  return Math.min(config.streakMax, config.streakStep * (streak - config.streakStart + 1));
}

function isOutOfRange(config: StreetHoopsConfig, e: ShotEvent): boolean {
  return (
    e.tMs < 0 ||
    e.tMs > config.durationMs - 1 ||
    e.aimX < AIM_X_MIN ||
    e.aimX > AIM_X_MAX ||
    e.power < POWER_MIN ||
    e.power > POWER_MAX
  );
}

/** Returns the first failing check of steps 1–9 in §7, or null if the run is valid. */
function firstFailure(input: EvaluateInput): EvaluateResult | null {
  const { config, session, submission } = input;
  const events = submission.events;
  const reject = (reason: RejectionReason): EvaluateResult => ({ outcome: "rejected", reason });

  if (session.status === "created") return reject("not_started");
  if (session.status === "expired" || submission.submittedAtMs > session.submitDeadlineMs) {
    return { outcome: "expired" };
  }
  if (submission.rulesetId !== input.rulesetId) return reject("ruleset_mismatch");
  // Step 4 (invalid_event) runs in the API on raw JSON tokens.
  if (events.some((e) => isOutOfRange(config, e))) return reject("out_of_range");
  if (events.length > config.maxEvents) return reject("too_many_events");
  for (let i = 1; i < events.length; i++) {
    if (events[i]!.tMs <= events[i - 1]!.tMs) return reject("non_monotonic");
  }
  for (let i = 1; i < events.length; i++) {
    if (events[i]!.tMs - events[i - 1]!.tMs < config.minGapMs) return reject("cadence_violation");
  }
  const maxT = events.length === 0 ? 0 : events[events.length - 1]!.tMs;
  if (submission.submittedAtMs < session.startedAtMs + maxT - config.earlyToleranceMs) {
    return reject("submitted_too_early");
  }
  return null;
}

/** Deterministic replay and scoring of one Street Hoops run (spec v1.3 §7). */
export function evaluate(input: EvaluateInput): EvaluateResult {
  const { config } = input;
  assertValidConfig(config);
  assertIntegerEvents(input.submission.events);

  const failure = firstFailure(input);
  if (failure !== null) return failure;

  const waypoints = generateWaypoints(config, input.seed);
  const shots: ShotResult[] = [];
  let score = 0;
  let streak = 0;
  let maxStreak = 0;

  for (const e of input.submission.events) {
    const { targetX, idealPower } = targetAt(config, waypoints, e.tMs);
    const dx = Math.abs(e.aimX - targetX);
    const dp = Math.abs(e.power - idealPower);

    let result: ShotResult["result"];
    let points: number;
    if (dx <= config.perfectDx && dp <= config.perfectDp) {
      streak++;
      result = "perfect";
      points = config.perfectPoints + streakBonus(config, streak);
    } else if (dx <= config.hitDx && dp <= config.hitDp) {
      streak++;
      result = "hit";
      points = config.hitPoints + streakBonus(config, streak);
    } else {
      streak = 0;
      result = "miss";
      points = 0;
    }

    maxStreak = Math.max(maxStreak, streak);
    score += points;
    shots.push({ tMs: e.tMs, targetX, idealPower, dx, dp, result, points, streak });
  }

  return {
    outcome: "verified",
    score,
    eventCount: shots.length,
    maxStreak,
    shots,
  };
}
