import { evaluate, generateWaypoints, targetAt, type ShotEvent } from "@vybe/game-rules";
import { describe, expect, it } from "vitest";
import fixture from "../../../../../packages/game-rules/fixtures/rulesets/street-hoops-v1.json";
import { addRun, EMPTY_STATS, evaluateTrace, rates, shotGate, type LocalRun } from "./trace";

const config = fixture.config;
const run: LocalRun = { config, rulesetId: fixture.id, seed: 20261106 };
const waypoints = generateWaypoints(config, run.seed);

function shotAt(tMs: number, aimOffset: number, powerOffset: number): ShotEvent {
  const { targetX, idealPower } = targetAt(config, waypoints, tMs);
  return { tMs, aimX: targetX + aimOffset, power: idealPower + powerOffset };
}

describe("evaluateTrace", () => {
  it("scores perfect, hit and miss shots through the reference simulator", () => {
    const events = [
      shotAt(1000, 0, 0),
      shotAt(2000, config.hitDx, 0),
      shotAt(3000, 0, -config.hitDp),
      shotAt(4000, config.hitDx + 1, 0),
    ];
    const result = evaluateTrace(run, events, 4000);
    expect(result.shots.map((s) => s.result)).toEqual(["perfect", "hit", "hit", "miss"]);
    expect(result.shots.map((s) => s.streak)).toEqual([1, 2, 3, 0]);
    expect(result.score).toBe(
      config.perfectPoints + 2 * config.hitPoints + config.streakStep,
    );
  });

  it("matches a direct evaluate() call with the server-shaped input", () => {
    const events = [shotAt(0, 10, -20), shotAt(650, -300, 0), shotAt(59_999, 0, 0)];
    const direct = evaluate({
      config,
      rulesetId: run.rulesetId,
      seed: run.seed,
      session: { status: "active", startedAtMs: 0, submitDeadlineMs: config.durationMs + config.graceMs },
      submission: { rulesetId: run.rulesetId, submittedAtMs: config.durationMs, events },
    });
    expect(evaluateTrace(run, events, config.durationMs)).toEqual(direct);
  });

  it("gives each shot the same result in a prefix as in the full trace", () => {
    const events = Array.from({ length: 20 }, (_, i) => shotAt(i * 2000, ((i * 37) % 9) * 30 - 120, ((i * 53) % 7) * 30 - 90));
    const full = evaluateTrace(run, events, config.durationMs).shots;
    events.forEach((e, i) => {
      const prefix = evaluateTrace(run, events.slice(0, i + 1), e.tMs).shots;
      expect(prefix[i]).toEqual(full[i]);
    });
  });

  it("scores an empty trace as 0", () => {
    expect(evaluateTrace(run, [], config.durationMs)).toMatchObject({ score: 0, eventCount: 0 });
  });

  it("throws when the trace would be rejected", () => {
    const events = [shotAt(1000, 0, 0), shotAt(1000 + config.minGapMs - 1, 0, 0)];
    expect(() => evaluateTrace(run, events, 2000)).toThrow(/cadence_violation/);
  });
});

describe("shotGate", () => {
  it("accepts a first shot at 0 and a shot exactly minGapMs after the previous one", () => {
    expect(shotGate(config, [], 0)).toBe("ok");
    expect(shotGate(config, [shotAt(1000, 0, 0)], 1000 + config.minGapMs)).toBe("ok");
  });

  it("refuses shots the simulator would reject", () => {
    expect(shotGate(config, [shotAt(1000, 0, 0)], 1000 + config.minGapMs - 1)).toBe("cooldown");
    expect(shotGate(config, [], config.durationMs)).toBe("time_up");
    const full = Array.from({ length: config.maxEvents }, (_, i) => shotAt(i * config.minGapMs, 0, 0));
    expect(shotGate(config, full, config.durationMs - 1)).toBe("max_events");
  });
});

describe("session stats", () => {
  it("counts perfects as hits and computes rates", () => {
    const result = evaluateTrace(run, [shotAt(0, 0, 0), shotAt(1000, config.hitDx, 0), shotAt(2000, 0, -config.hitDp - 1)], 2000);
    const stats = addRun(EMPTY_STATS, result.score, result.shots);
    expect(stats).toEqual({ runs: 1, shots: 3, hits: 2, perfects: 1, bestScore: result.score });
    expect(rates(stats).hitRate).toBeCloseTo(66.667, 2);
    expect(rates(stats).perfectRate).toBeCloseTo(33.333, 2);
    expect(rates(EMPTY_STATS)).toEqual({ hitRate: 0, perfectRate: 0 });
  });
});
