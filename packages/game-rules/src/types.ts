/** Street Hoops ruleset config (spec v1.3 §7). All values are integers. */
export interface StreetHoopsConfig {
  streamId: number;
  durationMs: number;
  graceMs: number;
  earlyToleranceMs: number;
  segmentMs: number;
  xMin: number;
  xMax: number;
  pMin: number;
  pMax: number;
  hitDx: number;
  hitDp: number;
  perfectDx: number;
  perfectDp: number;
  hitPoints: number;
  perfectPoints: number;
  streakStart: number;
  streakStep: number;
  streakMax: number;
  minGapMs: number;
  maxEvents: number;
  minShotsForRewards: number;
  /** Reward eligibility also needs `submittedAt − startedAt ≥ minRunMsForRewards` (API check, not a simulator check). */
  minRunMsForRewards: number;
  /** Base coins need `score ≥ minScoreForCoins` (API reward rule). */
  minScoreForCoins: number;
}

/** Fixed input units (§7). */
export const AIM_X_MIN = -1000;
export const AIM_X_MAX = 1000;
export const POWER_MIN = 0;
export const POWER_MAX = 1000;

export interface ShotEvent {
  tMs: number;
  aimX: number;
  power: number;
}

export interface Waypoint {
  x: number;
  p: number;
}

export type SessionStatus = "created" | "active" | "expired";

export interface EvaluateInput {
  config: StreetHoopsConfig;
  rulesetId: string;
  seed: number;
  session: {
    status: SessionStatus;
    startedAtMs: number;
    submitDeadlineMs: number;
  };
  submission: {
    rulesetId: string;
    submittedAtMs: number;
    events: ShotEvent[];
  };
}

export type RejectionReason =
  | "not_started"
  | "ruleset_mismatch"
  | "out_of_range"
  | "too_many_events"
  | "non_monotonic"
  | "cadence_violation"
  | "submitted_too_early";

export type ShotResultKind = "perfect" | "hit" | "miss";

export interface ShotResult {
  tMs: number;
  targetX: number;
  idealPower: number;
  dx: number;
  dp: number;
  result: ShotResultKind;
  points: number;
  streak: number;
}

export type EvaluateResult =
  | { outcome: "expired" }
  | { outcome: "rejected"; reason: RejectionReason }
  | {
      outcome: "verified";
      score: number;
      eventCount: number;
      maxStreak: number;
      shots: ShotResult[];
    };
