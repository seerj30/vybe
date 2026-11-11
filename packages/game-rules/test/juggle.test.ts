import { readdirSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { rulesetChecksum } from "../src/checksum.ts";
import { assertValidJuggleConfig, evaluateJuggle, gravityAt, juggleStreakBonus, StreetJuggleSim } from "../src/juggle.ts";
import { autoplay } from "../src/juggle-autoplay.ts";
import type { JuggleEvaluateInput, JuggleEvaluateResult, JuggleTouch, StreetJuggleConfig } from "../src/juggle-types.ts";
import { FIXTURES_DIR, readJson } from "./helpers.ts";

interface JuggleRulesetFile {
  id: string;
  gameId: string;
  config: StreetJuggleConfig;
  checksum: string;
}

const RULESET = readJson<JuggleRulesetFile>("rulesets/street-juggle-v1.json");
const CONFIG = RULESET.config;
/** Stream 54 with seed 42 gives the official pcg32-demo outputs, so the draws below are checked by hand. */
const DEMO = { ...CONFIG, streamId: 54 };
const DEMO_SEED = 42;

function input(events: JuggleTouch[], overrides: { status?: "created" | "active" | "expired"; submittedAtMs?: number } = {}): JuggleEvaluateInput {
  return {
    config: CONFIG,
    rulesetId: RULESET.id,
    seed: 12345,
    session: { status: overrides.status ?? "active", startedAtMs: 1000, submitDeadlineMs: 1000 + 65_000 },
    submission: { rulesetId: RULESET.id, submittedAtMs: overrides.submittedAtMs ?? 61_500, events },
  };
}

function verified(result: JuggleEvaluateResult): Extract<JuggleEvaluateResult, { outcome: "verified" }> {
  if (result.outcome !== "verified") throw new Error(JSON.stringify(result));
  return result;
}

describe("Street Juggle config", () => {
  it("has a matching checksum and passes validation", async () => {
    expect(RULESET.gameId).toBe("street-juggle");
    expect(await rulesetChecksum(CONFIG)).toBe(RULESET.checksum);
    expect(() => assertValidJuggleConfig(CONFIG)).not.toThrow();
  });

  it.each([
    ["a non-integer value", { goodDy: 1.5 }],
    ["streamId above uint32", { streamId: 2 ** 32 }],
    ["zero tick", { tickMs: 0 }],
    ["duration not a multiple of the tick", { durationMs: 60_005 }],
    ["respawn not a multiple of the tick", { respawnMs: 1005 }],
    ["lock not a multiple of the tick", { whiffLockMs: 455 }],
    ["level not a multiple of the tick", { levelMs: 12_005 }],
    ["no lanes", { lanes: 0 }],
    ["gap below the tick", { minGapMs: 9 }],
    ["perfect window wider than the good window", { perfectDy: 18_001 }],
    ["window below the ground", { goodDy: 28_000 }],
    ["serve inside the window", { serveY: 46_000 }],
    ["zero gravity", { gravityStart: 0 }],
    ["empty launch range", { launchMin: 1701 }],
    ["star chance above 100", { starPercent: 101 }],
    ["streakStart 0", { streakStart: 0 }],
  ])("rejects %s", (_label, patch) => {
    expect(() => assertValidJuggleConfig({ ...CONFIG, ...patch })).toThrow(RangeError);
  });
});

describe("StreetJuggleSim", () => {
  it("serves with the first two draws and falls under gravity 18", () => {
    // Draws: lane = 0xa15c02b7 mod 3 = 0; star roll = 0x7b47f409 mod 100 = 97 (not gold).
    const sim = new StreetJuggleSim(DEMO, DEMO_SEED);
    sim.advanceTo(1);
    expect(sim.ball).toEqual({ inPlay: true, lane: 0, y: 89_982, vy: -18, star: false });
    sim.advanceTo(80);
    // y after k ticks = 90000 − 9·k·(k + 1).
    expect(sim.ball.y).toBe(90_000 - 9 * 80 * 81);
  });

  it("scores a perfect kick and launches with the next three draws", () => {
    const sim = new StreetJuggleSim(DEMO, DEMO_SEED);
    // Tick 80: y = 31680, dy = 3680 ≤ 5000.
    expect(sim.touch({ tMs: 805, lane: 0 })).toEqual({
      tMs: 805, lane: 0, ballLane: 0, ballY: 31_680, result: "perfect", points: 150, streak: 1,
    });
    // vy = 1300 + 0xba1d3330 mod 401 = 1602, lane = 0x83d2f293 mod 3 = 1, roll = 0xbfa4784b mod 100 = 55.
    // The physics of tick 80 then run: vy = 1602 − 18, y = 31680 + 1584.
    expect(sim.ball).toEqual({ inPlay: true, lane: 1, y: 33_264, vy: 1584, star: false });
  });

  it.each([
    [69, "whiff"], // y = 46530: above the window
    [70, "good"], // y = 45270
    [79, "good"], // y = 33120, dy = 5120
    [80, "perfect"],
    [85, "perfect"], // y = 24210
    [86, "good"], // y = 22662, dy = 5338
  ])("judges a touch at tick %i as %s", (tick, expected) => {
    const sim = new StreetJuggleSim(DEMO, DEMO_SEED);
    expect(sim.touch({ tMs: tick * 10, lane: 0 }).result).toBe(expected);
  });

  it("whiffs a wrong spot, locks for 450 ms and does not extend the lock", () => {
    const sim = new StreetJuggleSim(DEMO, DEMO_SEED);
    expect(sim.touch({ tMs: 800, lane: 2 })).toMatchObject({ result: "whiff", streak: 0, ballLane: 0 });
    expect(sim.isLocked(124)).toBe(true);
    expect(sim.isLocked(125)).toBe(false);
    // Tick 85 is in the perfect window, but the player is off balance.
    expect(sim.touch({ tMs: 850, lane: 0 }).result).toBe("whiff");
    expect(sim.isLocked(125)).toBe(false);
  });

  it("drops the ball, waits respawnMs and serves again", () => {
    const sim = new StreetJuggleSim(DEMO, DEMO_SEED);
    // Tick 99 physics: y = 90000 − 9·100·101 < 0.
    sim.advanceTo(100);
    expect(sim.dropCount).toBe(1);
    expect(sim.ball.inPlay).toBe(false);
    expect(sim.touch({ tMs: 1500, lane: 1 })).toMatchObject({ ballLane: -1, ballY: 0, result: "whiff" });
    sim.advanceTo(200);
    expect(sim.ball.inPlay).toBe(false);
    // respawnTick = 99 + 1 + 100 = 200; the serve draws 0xba1d3330 mod 3 = 2 and roll 55.
    sim.advanceTo(201);
    expect(sim.ball).toMatchObject({ inPlay: true, lane: 2, y: 90_000 - 18, star: false });
  });

  it("adds the gold-ball bonus and the streak bonus", () => {
    const gold = { ...DEMO, starPercent: 100 };
    const result = verified(evaluateJuggle({ ...input(autoplay(gold, 9, { maxTouches: 5 })), config: gold, seed: 9 }));
    expect(result.touches.map((t) => t.points)).toEqual([250, 250, 275, 300, 325]);
    expect(result.maxStreak).toBe(5);
  });

  it("raises gravity by gravityStep every levelMs", () => {
    expect([0, 1199, 1200, 2399, 2400, 5999].map((k) => gravityAt(CONFIG, k))).toEqual([18, 18, 21, 21, 24, 30]);
  });

  it("caps the streak bonus like Street Hoops", () => {
    expect([1, 2, 3, 4, 5, 6, 50].map((s) => juggleStreakBonus(CONFIG, s))).toEqual([0, 0, 25, 50, 75, 100, 100]);
  });

  it("refuses a touch before the simulation time", () => {
    const sim = new StreetJuggleSim(CONFIG, 1);
    sim.advanceTo(100);
    expect(() => sim.touch({ tMs: 990, lane: 0 })).toThrow(RangeError);
    expect(() => sim.touch({ tMs: 60_000, lane: 0 })).toThrow(RangeError);
  });

  it("gives the same result live and in a replay", () => {
    const trace = autoplay(CONFIG, 4242, { offsetsMs: [0, 40, -60, 90, -20] });
    const live = new StreetJuggleSim(CONFIG, 4242);
    for (const event of trace) {
      live.advanceTo(Math.floor(event.tMs / CONFIG.tickMs) - 3); // frames run ahead of the touch
      live.touch(event);
    }
    live.advanceTo(live.totalTicks);
    const replay = verified(evaluateJuggle({ ...input(trace), seed: 4242 }));
    expect(live.score).toBe(replay.score);
    expect(live.touches).toEqual(replay.touches);
  });
});

describe("evaluateJuggle: check order", () => {
  it("throws on non-integer fields (the API rejects them first)", () => {
    expect(() => evaluateJuggle(input([{ tMs: 0, lane: 0.5 }]))).toThrow(TypeError);
  });

  it("applies the Street Hoops check order with Street Juggle limits", () => {
    expect(evaluateJuggle(input([], { status: "created" }))).toEqual({ outcome: "rejected", reason: "not_started" });
    expect(evaluateJuggle(input([], { submittedAtMs: 66_001 }))).toEqual({ outcome: "expired" });
    expect(evaluateJuggle(input([{ tMs: 0, lane: 3 }]))).toEqual({ outcome: "rejected", reason: "out_of_range" });
    const tooMany = Array.from({ length: 401 }, (_, i) => ({ tMs: i * 149, lane: 1 }));
    expect(evaluateJuggle(input(tooMany))).toEqual({ outcome: "rejected", reason: "too_many_events" });
    expect(evaluateJuggle(input([{ tMs: 5, lane: 0 }, { tMs: 5, lane: 1 }]))).toEqual({ outcome: "rejected", reason: "non_monotonic" });
    expect(evaluateJuggle(input([{ tMs: 0, lane: 0 }, { tMs: 149, lane: 1 }]))).toEqual({ outcome: "rejected", reason: "cadence_violation" });
    expect(verified(evaluateJuggle(input([{ tMs: 0, lane: 0 }, { tMs: 150, lane: 1 }, { tMs: 59_999, lane: 2 }]))).eventCount).toBe(3);
  });
});

describe("Street Juggle golden fixtures", () => {
  for (const dir of ["street-juggle/scoring", "street-juggle/rejections"]) {
    const names = readdirSync(`${FIXTURES_DIR}${dir}`).filter((f) => f.endsWith(".json")).sort();

    it(`has ${dir} fixtures`, () => {
      expect(names.length).toBeGreaterThan(10);
    });

    for (const file of names) {
      it(`${dir}/${file}`, () => {
        const fixture = readJson<{ input: JuggleEvaluateInput; expected: JuggleEvaluateResult }>(`${dir}/${file}`);
        expect(evaluateJuggle(fixture.input)).toEqual(fixture.expected);
      });
    }
  }
});
