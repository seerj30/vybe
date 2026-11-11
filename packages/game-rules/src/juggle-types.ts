import type { RejectionReason, SessionStatus } from "./types.ts";

/** Street Juggle ruleset config (docs/design/p1-second-game.md §2.2). All values are integers. */
export interface StreetJuggleConfig {
  streamId: number;
  durationMs: number;
  graceMs: number;
  earlyToleranceMs: number;
  tickMs: number;
  lanes: number;
  serveY: number;
  kickY: number;
  goodDy: number;
  perfectDy: number;
  gravityStart: number;
  gravityStep: number;
  levelMs: number;
  launchMin: number;
  launchMax: number;
  respawnMs: number;
  whiffLockMs: number;
  goodPoints: number;
  perfectPoints: number;
  starPercent: number;
  starPoints: number;
  streakStart: number;
  streakStep: number;
  streakMax: number;
  minGapMs: number;
  maxEvents: number;
  /** Reward eligibility needs at least this many touches (API rule, not a simulator check). */
  minEventsForRewards: number;
  /** Reward eligibility also needs `submittedAt − startedAt ≥ minRunMsForRewards` (API rule). */
  minRunMsForRewards: number;
  /** Base coins need `score ≥ minScoreForCoins` (API reward rule). */
  minScoreForCoins: number;
}

/** One touch of a spot: `lane` 0 = left, 1 = centre, 2 = right. */
export interface JuggleTouch {
  tMs: number;
  lane: number;
}

export type JuggleTouchKind = "perfect" | "good" | "whiff";

export interface JuggleTouchResult {
  tMs: number;
  lane: number;
  /** Lane of the ball at the touch; −1 while the ball waits for its serve. */
  ballLane: number;
  /** Height of the ball at the touch; 0 while the ball waits. */
  ballY: number;
  result: JuggleTouchKind;
  points: number;
  streak: number;
}

export interface JuggleEvaluateInput {
  config: StreetJuggleConfig;
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
    events: JuggleTouch[];
  };
}

export type JuggleEvaluateResult =
  | { outcome: "expired" }
  | { outcome: "rejected"; reason: RejectionReason }
  | {
      outcome: "verified";
      score: number;
      eventCount: number;
      maxStreak: number;
      kickCount: number;
      perfectCount: number;
      dropCount: number;
      touches: JuggleTouchResult[];
    };

/** The ball as the client draws it. */
export interface JuggleBall {
  inPlay: boolean;
  lane: number;
  y: number;
  vy: number;
  star: boolean;
}
