import { Pcg32 } from "./pcg32.ts";
import type { RejectionReason } from "./types.ts";
import type {
  JuggleBall,
  JuggleEvaluateInput,
  JuggleEvaluateResult,
  JuggleTouch,
  JuggleTouchKind,
  JuggleTouchResult,
  StreetJuggleConfig,
} from "./juggle-types.ts";

const UINT32_MAX = 0xffffffff;
/** Bounds that keep every simulation value an exact integer in C# `long` and JS `number`. */
const MAX_DURATION_MS = 600_000;
const MAX_MAGNITUDE = 1_000_000;

function fail(message: string): never {
  throw new RangeError(`config.${message}`);
}

/** Validates the config invariants the simulator depends on (docs/design/p1-second-game.md §2.2). */
export function assertValidJuggleConfig(config: StreetJuggleConfig): void {
  for (const [key, value] of Object.entries(config)) {
    if (!Number.isSafeInteger(value)) fail(`${key} must be an integer`);
  }
  const c = config;
  if (c.streamId < 0 || c.streamId > UINT32_MAX) fail("streamId must be uint32");
  if (c.tickMs <= 0) fail("tickMs must be positive");
  if (c.durationMs <= 0 || c.durationMs > MAX_DURATION_MS || c.durationMs % c.tickMs !== 0) {
    fail("durationMs must be a positive multiple of tickMs, at most 600000");
  }
  for (const key of ["respawnMs", "whiffLockMs"] as const) {
    if (c[key] < 0 || c[key] % c.tickMs !== 0) fail(`${key} must be a non-negative multiple of tickMs`);
  }
  if (c.levelMs <= 0 || c.levelMs % c.tickMs !== 0) fail("levelMs must be a positive multiple of tickMs");
  if (c.lanes < 1) fail("lanes must be at least 1");
  if (c.minGapMs < c.tickMs || c.maxEvents < 0) fail("minGapMs must be at least tickMs and maxEvents non-negative");
  if (c.perfectDy < 0 || c.perfectDy > c.goodDy) fail("perfectDy must be in [0, goodDy]");
  if (c.kickY - c.goodDy <= 0 || c.serveY <= c.kickY + c.goodDy || c.serveY > MAX_MAGNITUDE) {
    fail("heights must satisfy 0 < kickY - goodDy and kickY + goodDy < serveY <= 1000000");
  }
  if (c.gravityStart <= 0 || c.gravityStep < 0 || c.gravityStart + c.gravityStep * (c.durationMs / c.levelMs) > MAX_MAGNITUDE) {
    fail("gravity must be positive and at most 1000000");
  }
  if (c.launchMin <= 0 || c.launchMin > c.launchMax || c.launchMax > MAX_MAGNITUDE) {
    fail("launch range must be in (0, 1000000]");
  }
  if (c.starPercent < 0 || c.starPercent > 100) fail("starPercent must be in [0, 100]");
  if (c.goodPoints < 0 || c.perfectPoints < 0 || c.starPoints < 0 || c.streakStep < 0 || c.streakMax < 0) {
    fail("points must be non-negative");
  }
  if (c.streakStart < 1) fail("streakStart must be at least 1");
}

export function juggleStreakBonus(config: StreetJuggleConfig, streak: number): number {
  if (streak < config.streakStart) return 0;
  return Math.min(config.streakMax, config.streakStep * (streak - config.streakStart + 1));
}

/** Gravity per tick² at tick `k` (one level per `levelMs`). */
export function gravityAt(config: StreetJuggleConfig, tick: number): number {
  return config.gravityStart + config.gravityStep * Math.floor((tick * config.tickMs) / config.levelMs);
}

/**
 * The Street Juggle simulation (design §2.3–2.4). The client runs it live; `evaluateJuggle` replays a
 * whole trace with it, so live feedback and the server replay use the same steps.
 */
export class StreetJuggleSim {
  readonly totalTicks: number;
  private readonly rng: Pcg32;
  /** Ticks 0 … nextTick − 1 are done. */
  private nextTick = 0;
  private inPlay = false;
  private ballLane = 0;
  private y = 0;
  private vy = 0;
  private star = false;
  private respawnTick = 0;
  private lockUntilTick = 0;
  private streak = 0;
  private lastTouchTMs = -1;

  score = 0;
  maxStreak = 0;
  kickCount = 0;
  perfectCount = 0;
  dropCount = 0;
  readonly touches: JuggleTouchResult[] = [];
  readonly config: StreetJuggleConfig;

  constructor(config: StreetJuggleConfig, seed: number) {
    assertValidJuggleConfig(config);
    this.config = config;
    if (!Number.isInteger(seed) || seed < 0 || seed > UINT32_MAX) {
      throw new RangeError("seed must be uint32");
    }
    this.rng = new Pcg32(BigInt(seed), BigInt(config.streamId));
    this.totalTicks = config.durationMs / config.tickMs;
  }

  /** The next tick to run; the ball state is the state after tick `currentTick − 1`. */
  get currentTick(): number {
    return this.nextTick;
  }

  get ball(): JuggleBall {
    return { inPlay: this.inPlay, lane: this.ballLane, y: this.y, vy: this.vy, star: this.star };
  }

  get currentStreak(): number {
    return this.streak;
  }

  /** True while a whiff keeps the player off balance at `tick`. */
  isLocked(tick: number): boolean {
    return tick < this.lockUntilTick;
  }

  /** Runs every tick before `tick` without touches (`tick` is clamped to the end of the run). */
  advanceTo(tick: number): void {
    const end = Math.min(tick, this.totalTicks);
    while (this.nextTick < end) this.runTick(null);
  }

  /** Runs the ticks up to the touch, then the touch tick with the touch. The caller checks cadence and ranges. */
  touch(event: JuggleTouch): JuggleTouchResult {
    const tick = Math.floor(event.tMs / this.config.tickMs);
    if (!Number.isSafeInteger(event.tMs) || !Number.isSafeInteger(event.lane) || tick >= this.totalTicks) {
      throw new RangeError(`touch outside the run: ${JSON.stringify(event)}`);
    }
    if (tick < this.nextTick || event.tMs <= this.lastTouchTMs) {
      throw new RangeError(`touch at ${event.tMs} ms is before the simulation time`);
    }
    this.advanceTo(tick);
    this.lastTouchTMs = event.tMs;
    const result = this.runTick(event);
    if (result === null) throw new Error("unreachable: a touch tick returns a result");
    return result;
  }

  private runTick(event: JuggleTouch | null): JuggleTouchResult | null {
    const c = this.config;
    const k = this.nextTick;
    // 1. Serve.
    if (!this.inPlay && k >= this.respawnTick) {
      this.ballLane = this.rng.nextInRange(0, c.lanes - 1);
      this.star = this.rng.nextInRange(0, 99) < c.starPercent;
      this.y = c.serveY;
      this.vy = 0;
      this.inPlay = true;
    }
    // 2. Touch.
    const result = event === null ? null : this.applyTouch(event, k);
    // 3. Physics.
    if (this.inPlay) {
      this.vy -= gravityAt(c, k);
      this.y += this.vy;
      if (this.y <= 0) {
        this.streak = 0;
        this.dropCount++;
        this.inPlay = false;
        this.respawnTick = k + 1 + c.respawnMs / c.tickMs;
      }
    }
    this.nextTick = k + 1;
    return result;
  }

  private applyTouch(event: JuggleTouch, k: number): JuggleTouchResult {
    const c = this.config;
    const ballLane = this.inPlay ? this.ballLane : -1;
    const ballY = this.inPlay ? this.y : 0;
    const dy = Math.abs(this.y - c.kickY);
    const kick = k >= this.lockUntilTick && this.inPlay && event.lane === this.ballLane && this.vy <= 0 && dy <= c.goodDy;

    let result: JuggleTouchKind;
    let points = 0;
    if (kick) {
      result = dy <= c.perfectDy ? "perfect" : "good";
      this.streak++;
      points = (result === "perfect" ? c.perfectPoints : c.goodPoints) + juggleStreakBonus(c, this.streak) + (this.star ? c.starPoints : 0);
      this.kickCount++;
      if (result === "perfect") this.perfectCount++;
      // Launch: vy, lane, star (design §2.4 draw order). The ball leaves from the touch height.
      this.vy = this.rng.nextInRange(c.launchMin, c.launchMax);
      this.ballLane = this.rng.nextInRange(0, c.lanes - 1);
      this.star = this.rng.nextInRange(0, 99) < c.starPercent;
    } else {
      result = "whiff";
      this.streak = 0;
      if (k >= this.lockUntilTick) this.lockUntilTick = k + c.whiffLockMs / c.tickMs;
    }
    this.score += points;
    this.maxStreak = Math.max(this.maxStreak, this.streak);
    const touch: JuggleTouchResult = { tMs: event.tMs, lane: event.lane, ballLane, ballY, result, points, streak: this.streak };
    this.touches.push(touch);
    return touch;
  }
}

/** The API's invalid_event check (design §4 step 4) must run first; a non-integer here is a caller bug. */
function assertIntegerEvents(events: readonly JuggleTouch[]): void {
  for (const e of events) {
    if (!Number.isSafeInteger(e.tMs) || !Number.isSafeInteger(e.lane)) {
      throw new TypeError("events must contain only integers; run the invalid_event check first");
    }
  }
}

function isOutOfRange(config: StreetJuggleConfig, e: JuggleTouch): boolean {
  return e.tMs < 0 || e.tMs > config.durationMs - 1 || e.lane < 0 || e.lane > config.lanes - 1;
}

/** Returns the first failing check of design §4 (steps 1–3, 5–9), or null if the run is valid. */
function firstFailure(input: JuggleEvaluateInput): JuggleEvaluateResult | null {
  const { config, session, submission } = input;
  const events = submission.events;
  const reject = (reason: RejectionReason): JuggleEvaluateResult => ({ outcome: "rejected", reason });

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

/** Deterministic replay and scoring of one Street Juggle run (design §2–4). */
export function evaluateJuggle(input: JuggleEvaluateInput): JuggleEvaluateResult {
  assertValidJuggleConfig(input.config);
  assertIntegerEvents(input.submission.events);

  const failure = firstFailure(input);
  if (failure !== null) return failure;

  const sim = new StreetJuggleSim(input.config, input.seed);
  for (const event of input.submission.events) sim.touch(event);
  sim.advanceTo(sim.totalTicks);
  return {
    outcome: "verified",
    score: sim.score,
    eventCount: sim.touches.length,
    maxStreak: sim.maxStreak,
    kickCount: sim.kickCount,
    perfectCount: sim.perfectCount,
    dropCount: sim.dropCount,
    touches: sim.touches,
  };
}
