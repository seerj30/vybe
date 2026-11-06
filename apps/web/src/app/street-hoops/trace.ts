import {
  evaluate,
  type EvaluateInput,
  type EvaluateResult,
  type ShotEvent,
  type ShotResult,
  type StreetHoopsConfig,
} from "@vybe/game-rules";

export interface LocalRun {
  config: StreetHoopsConfig;
  rulesetId: string;
  seed: number;
}

export type VerifiedResult = Extract<EvaluateResult, { outcome: "verified" }>;

/** Why the client does not record a release as a shot event. */
export type ShotGate = "ok" | "time_up" | "max_events" | "cooldown";

/** Mirrors the simulator's range, count and cadence checks so a recorded trace is never rejected. */
export function shotGate(config: StreetHoopsConfig, events: readonly ShotEvent[], tMs: number): ShotGate {
  if (tMs < 0 || tMs > config.durationMs - 1) return "time_up";
  if (events.length >= config.maxEvents) return "max_events";
  const last = events[events.length - 1];
  if (last !== undefined && tMs - last.tMs < config.minGapMs) return "cooldown";
  return "ok";
}

/**
 * Scores the trace with the reference simulator, as the server will (spec §7).
 * The client clock starts at 0, so `startedAtMs` is 0 and `submittedAtMs` is the elapsed time.
 */
export function evaluateTrace(run: LocalRun, events: readonly ShotEvent[], submittedAtMs: number): VerifiedResult {
  const input: EvaluateInput = {
    config: run.config,
    rulesetId: run.rulesetId,
    seed: run.seed,
    session: { status: "active", startedAtMs: 0, submitDeadlineMs: run.config.durationMs + run.config.graceMs },
    submission: { rulesetId: run.rulesetId, submittedAtMs, events: [...events] },
  };
  const result = evaluate(input);
  if (result.outcome !== "verified") {
    const detail = result.outcome === "rejected" ? `rejected: ${result.reason}` : "expired";
    throw new Error(`Local trace was ${detail}; the client recorded an invalid trace`);
  }
  return result;
}

export interface SessionStats {
  runs: number;
  shots: number;
  /** Hits including perfects. */
  hits: number;
  perfects: number;
  bestScore: number;
}

export const EMPTY_STATS: SessionStats = { runs: 0, shots: 0, hits: 0, perfects: 0, bestScore: 0 };

export function addRun(stats: SessionStats, score: number, shots: readonly ShotResult[]): SessionStats {
  return {
    runs: stats.runs + 1,
    shots: stats.shots + shots.length,
    hits: stats.hits + shots.filter((s) => s.result !== "miss").length,
    perfects: stats.perfects + shots.filter((s) => s.result === "perfect").length,
    bestScore: Math.max(stats.bestScore, score),
  };
}

/** Hit and perfect rates in percent of all shots; 0 when there are no shots. */
export function rates(stats: SessionStats): { hitRate: number; perfectRate: number } {
  if (stats.shots === 0) return { hitRate: 0, perfectRate: 0 };
  return { hitRate: (100 * stats.hits) / stats.shots, perfectRate: (100 * stats.perfects) / stats.shots };
}
