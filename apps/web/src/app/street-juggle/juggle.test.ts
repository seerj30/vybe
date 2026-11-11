import { autoplay } from "../../../../../packages/game-rules/src/juggle-autoplay";
import { evaluateJuggle, StreetJuggleSim } from "@vybe/game-rules";
import { describe, expect, it } from "vitest";
import { laneForKey, laneFromFraction, touchGate, touchTime } from "./juggle-input";
import { applyJuggleOverrides, loadJuggleRuleset, verifyJuggleRuleset } from "./juggle-ruleset";
import { juggleNoCoinsText, juggleRejectionText } from "./juggle-text";
import { ballLanePosition, flightTicks, heightY, laneX, touchLabel, VIEW_HEIGHT } from "./juggle-view";

describe("Street Juggle ruleset", () => {
  it("loads the street-juggle-v1 fixture with a valid checksum", async () => {
    const ruleset = await loadJuggleRuleset();
    expect(ruleset.id).toBe("street-juggle-v1");
    expect(ruleset.config.lanes).toBe(3);
  });

  it("rejects a tampered config and invalid overrides", async () => {
    const ruleset = await loadJuggleRuleset();
    await expect(verifyJuggleRuleset({ ...ruleset, config: { ...ruleset.config, goodDy: 1 } })).rejects.toThrow(/checksum mismatch/);
    expect(applyJuggleOverrides(ruleset.config, { goodDy: 20_000 }).goodDy).toBe(20_000);
    expect(() => applyJuggleOverrides(ruleset.config, { perfectDy: 30_000 })).toThrow(RangeError);
    expect(() => applyJuggleOverrides(ruleset.config, { whiffLockMs: 455 })).toThrow(RangeError);
  });
});

describe("Street Juggle input", () => {
  it("maps keys to spots", () => {
    expect(["ArrowLeft", "KeyA", "ArrowDown", "KeyS", "Space", "ArrowRight", "KeyD"].map((k) => laneForKey(k, 3))).toEqual([
      0, 0, 1, 1, 1, 2, 2,
    ]);
    expect(laneForKey("KeyQ", 3)).toBeNull();
    expect(laneForKey("ArrowRight", 2)).toBeNull();
    expect(laneForKey("ArrowRight", 1)).toBe(0);
  });

  it("maps a pointer position to a column", () => {
    expect([0, 0.33, 0.34, 0.66, 0.67, 1].map((f) => laneFromFraction(f, 3))).toEqual([0, 0, 1, 1, 2, 2]);
    expect(laneFromFraction(-0.2, 3)).toBe(0);
    expect(() => laneFromFraction(Number.NaN, 3)).toThrow(RangeError);
  });

  it("gates touches like the server checks", async () => {
    const { config } = await loadJuggleRuleset();
    expect(touchGate(config, [], 0)).toBe("ok");
    expect(touchGate(config, [], 60_000)).toBe("time_up");
    expect(touchGate(config, [{ tMs: 1000, lane: 1 }], 1149)).toBe("cooldown");
    expect(touchGate(config, [{ tMs: 1000, lane: 1 }], 1150)).toBe("ok");
    const full = Array.from({ length: config.maxEvents }, (_, i) => ({ tMs: i * 150, lane: 1 }));
    expect(touchGate(config, full, 59_999)).toBe("max_events");
  });

  it("moves an early press to the next tick the simulation has not run", () => {
    expect(touchTime(1234.7, 100, 10)).toBe(1234);
    expect(touchTime(995.2, 100, 10)).toBe(1000);
  });

  it("keeps a live trace recorded through the gate valid on the server", async () => {
    const { config } = await loadJuggleRuleset();
    const trace = autoplay(config, 77, { offsetsMs: [0, 30, -50] });
    const sim = new StreetJuggleSim(config, 77);
    for (const touch of trace) sim.touch(touch);
    const replay = evaluateJuggle({
      config,
      rulesetId: "street-juggle-v1",
      seed: 77,
      session: { status: "active", startedAtMs: 0, submitDeadlineMs: 65_000 },
      submission: { rulesetId: "street-juggle-v1", submittedAtMs: 60_000, events: trace },
    });
    expect(replay.outcome === "verified" && replay.score).toBe(sim.score);
  });
});

describe("Street Juggle view", () => {
  it("maps lanes and heights to the screen", () => {
    expect([0, 1, 2].map((l) => laneX(l, 3, 300))).toEqual([50, 150, 250]);
    expect(heightY(0, 800, 100)).toBe(800);
    expect(heightY(VIEW_HEIGHT, 800, 100)).toBe(100);
    expect(heightY(VIEW_HEIGHT * 2, 800, 100)).toBe(100);
    expect(heightY(-5, 800, 100)).toBe(800);
  });

  it("moves the ball sideways during its flight only", () => {
    const flight = { fromLane: 0, toLane: 2, startTick: 100, ticks: 100 };
    expect(ballLanePosition(flight, 2, 100)).toBe(0);
    expect(ballLanePosition(flight, 2, 150)).toBe(1);
    expect(ballLanePosition(flight, 2, 400)).toBe(2);
    expect(ballLanePosition(flight, 1, 150)).toBe(1); // a new serve: no flight
    expect(ballLanePosition(null, 1, 150)).toBe(1);
  });

  it("estimates the flight time from the launch speed", async () => {
    const { config } = await loadJuggleRuleset();
    expect(flightTicks(config, 1800, 0)).toBe(200);
    expect(flightTicks(config, 0, 0)).toBe(1);
  });

  it("labels results and explains no-coin runs with the ruleset numbers", async () => {
    const { config } = await loadJuggleRuleset();
    const base = { tMs: 0, lane: 1, ballLane: 1, ballY: 28_000 };
    expect(touchLabel({ ...base, result: "perfect", points: 175, streak: 3 })).toBe("Perfect +175 · streak 3");
    expect(touchLabel({ ...base, result: "whiff", points: 0, streak: 0 })).toBe("Whiff · streak 0");
    expect(juggleNoCoinsText("too_few_shots", config)).toMatch(/at least 5 times/);
    expect(juggleNoCoinsText("score_below_minimum", config)).toMatch(/below 500/);
    expect(juggleRejectionText("cadence_violation")).toBe("touches came too fast");
  });
});
