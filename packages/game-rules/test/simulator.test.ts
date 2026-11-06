import { describe, expect, it } from "vitest";
import { Pcg32 } from "../src/pcg32.ts";
import {
  assertValidConfig,
  evaluate,
  generateWaypoints,
  streakBonus,
  targetAt,
  waypointCount,
} from "../src/simulator.ts";
import { canonicalJson, rulesetChecksum } from "../src/checksum.ts";
import type { ShotEvent, Waypoint } from "../src/types.ts";
import { CONFIG, makeInput, STARTED_AT_MS, SUBMIT_DEADLINE_MS } from "./helpers.ts";

/** Shots that aim exactly at the target, every `gapMs`, starting at t = 0. */
function perfectTrace(seed: number, count: number, gapMs: number): ShotEvent[] {
  const waypoints = generateWaypoints(CONFIG, seed);
  return Array.from({ length: count }, (_, i) => {
    const tMs = i * gapMs;
    const { targetX, idealPower } = targetAt(CONFIG, waypoints, tMs);
    return { tMs, aimX: targetX, power: idealPower };
  });
}

describe("Pcg32", () => {
  it("matches the official pcg32-demo vector for initstate 42, initseq 54", () => {
    const rng = new Pcg32(42n, 54n);
    const outputs = Array.from({ length: 6 }, () => rng.nextUint32());
    expect(outputs).toEqual([
      0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e,
    ]);
  });

  it("draws inside the inclusive range", () => {
    const rng = new Pcg32(7n, 54n);
    for (let i = 0; i < 1000; i++) {
      const v = rng.nextInRange(-700, 700);
      expect(v).toBeGreaterThanOrEqual(-700);
      expect(v).toBeLessThanOrEqual(700);
    }
  });

  it("rejects an empty range", () => {
    expect(() => new Pcg32(1n, 1n).nextInRange(5, 4)).toThrow(RangeError);
  });
});

describe("config", () => {
  it("derives 16 waypoints for the v1 ruleset", () => {
    expect(waypointCount(CONFIG)).toBe(16);
  });

  it("rejects a duration that is not a multiple of the segment", () => {
    expect(() => assertValidConfig({ ...CONFIG, segmentMs: 7000 })).toThrow(RangeError);
  });

  it("rejects a non-integer value", () => {
    expect(() => assertValidConfig({ ...CONFIG, hitDx: 160.5 })).toThrow(RangeError);
  });

  it.each([
    ["streamId above uint32", { streamId: 2 ** 32 }],
    ["negative streamId", { streamId: -1 }],
    ["empty x range", { xMin: 1, xMax: 0 }],
    ["empty p range", { pMin: 901, pMax: 900 }],
    ["zero segment", { segmentMs: 0 }],
    ["non-positive duration", { durationMs: -8000 }],
    ["zero gap", { minGapMs: 0 }],
    ["negative maxEvents", { maxEvents: -1 }],
  ])("rejects %s", (_label, patch) => {
    expect(() => assertValidConfig({ ...CONFIG, ...patch })).toThrow(RangeError);
  });
});

describe("waypoints", () => {
  it("matches literals derived by hand from the official PCG32 vector (seed 42, stream 54)", () => {
    // Outputs 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e
    // -> x = -700 + out % 1401, p = 300 + out % 601, drawn interleaved.
    expect(generateWaypoints(CONFIG, 42).slice(0, 3)).toEqual([
      { x: 377, p: 745 },
      { x: 577, p: 522 },
      { x: -96, p: 535 },
    ]);
  });

  it("draws interleaved x, p values from one PCG32 stream", () => {
    const rng = new Pcg32(99n, BigInt(CONFIG.streamId));
    const expected: Waypoint[] = [];
    for (let k = 0; k < 16; k++) {
      expected.push({ x: rng.nextInRange(-700, 700), p: rng.nextInRange(300, 900) });
    }
    expect(generateWaypoints(CONFIG, 99)).toEqual(expected);
  });

  it("accepts the uint32 seed bounds and rejects values outside them", () => {
    expect(generateWaypoints(CONFIG, 0)).toHaveLength(16);
    expect(generateWaypoints(CONFIG, 0xffffffff)).toHaveLength(16);
    expect(() => generateWaypoints(CONFIG, -1)).toThrow(RangeError);
    expect(() => generateWaypoints(CONFIG, 2 ** 32)).toThrow(RangeError);
  });
});

describe("targetAt", () => {
  const waypoints: Waypoint[] = [
    { x: -700, p: 900 },
    { x: 700, p: 300 },
    ...Array.from({ length: 14 }, () => ({ x: 0, p: 600 })),
  ];

  it("starts at the first waypoint", () => {
    expect(targetAt(CONFIG, waypoints, 0)).toEqual({ targetX: -700, idealPower: 900 });
  });

  it("rounds toward zero for positive and negative deltas", () => {
    // x: -700 + trunc(1400 * 3999 / 4000) = -700 + trunc(1399.65) = 699
    // p:  900 + trunc(-600 * 3999 / 4000) =  900 + trunc(-599.85) = 301 (floor would give 300)
    expect(targetAt(CONFIG, waypoints, 3999)).toEqual({ targetX: 699, idealPower: 301 });
  });

  it("reaches the next waypoint at the segment boundary", () => {
    expect(targetAt(CONFIG, waypoints, 4000)).toEqual({ targetX: 700, idealPower: 300 });
  });

  it("covers the last millisecond and rejects the end of the game", () => {
    expect(() => targetAt(CONFIG, waypoints, 59_999)).not.toThrow();
    expect(() => targetAt(CONFIG, waypoints, 60_000)).toThrow(RangeError);
  });
});

describe("streakBonus", () => {
  it("grows by 25 from the third hit and caps at 100", () => {
    expect([1, 2, 3, 4, 5, 6, 50].map((s) => streakBonus(CONFIG, s))).toEqual([
      0, 0, 25, 50, 75, 100, 100,
    ]);
  });
});

describe("evaluate: scoring", () => {
  it("gives the maximum score of 22,900 for 93 consecutive perfects", () => {
    const result = evaluate(makeInput(perfectTrace(12345, 93, 650)));
    expect(result).toMatchObject({ outcome: "verified", score: 22_900, eventCount: 93, maxStreak: 93 });
  });

  it("scores an empty trace as a verified 0", () => {
    expect(evaluate(makeInput([]))).toEqual({
      outcome: "verified",
      score: 0,
      eventCount: 0,
      maxStreak: 0,
      shots: [],
    });
  });

  it("applies the threshold boundaries exactly", () => {
    const [base] = perfectTrace(12345, 1, 650);
    const cases: Array<[number, number, string]> = [
      [50, 0, "perfect"],
      [51, 0, "hit"],
      [0, -50, "perfect"],
      [0, -51, "hit"],
      [160, 0, "hit"],
      [161, 0, "miss"],
      [0, -120, "hit"],
      [0, -121, "miss"],
    ];
    for (const [ddx, ddp, expected] of cases) {
      const event = { tMs: base!.tMs, aimX: base!.aimX + ddx, power: base!.power + ddp };
      const result = evaluate(makeInput([event]));
      expect(result.outcome === "verified" && result.shots[0]?.result).toBe(expected);
    }
  });

  it("resets the streak on a miss", () => {
    const trace = perfectTrace(12345, 6, 1000);
    trace[3] = { ...trace[3]!, aimX: trace[3]!.aimX > 0 ? -1000 : 1000 };
    const result = evaluate(makeInput(trace));
    if (result.outcome !== "verified") throw new Error(result.outcome);
    expect(result.shots.map((s) => s.streak)).toEqual([1, 2, 3, 0, 1, 2]);
    expect(result.shots.map((s) => s.points)).toEqual([150, 150, 175, 0, 150, 150]);
    expect(result.score).toBe(775);
    expect(result.maxStreak).toBe(3);
  });
});

describe("evaluate: check order", () => {
  const valid = perfectTrace(12345, 3, 1000);

  it("throws on non-integer event fields (the API must reject them first)", () => {
    expect(() => evaluate(makeInput([{ tMs: 0, aimX: Number.NaN, power: 500 }]))).toThrow(TypeError);
    expect(() => evaluate(makeInput([{ tMs: 0, aimX: 1.5, power: 500 }]))).toThrow(TypeError);
  });

  it("expires a session that is already expired", () => {
    expect(evaluate(makeInput(valid, { status: "expired" }))).toEqual({ outcome: "expired" });
  });

  it("accepts a gap of exactly minGapMs and the last millisecond", () => {
    const events = [
      { tMs: 59_349, aimX: 0, power: 500 },
      { tMs: 59_999, aimX: 0, power: 500 },
    ];
    expect(evaluate(makeInput(events)).outcome).toBe("verified");
  });

  it("rejects a session that was not started", () => {
    expect(evaluate(makeInput(valid, { status: "created" }))).toEqual({
      outcome: "rejected",
      reason: "not_started",
    });
  });

  it("expires a late submit, also when the ruleset is wrong", () => {
    const late = makeInput(valid, { submittedAtMs: SUBMIT_DEADLINE_MS + 1, submittedRulesetId: "x" });
    expect(evaluate(late)).toEqual({ outcome: "expired" });
  });

  it("accepts a submit exactly at the deadline", () => {
    expect(evaluate(makeInput(valid, { submittedAtMs: SUBMIT_DEADLINE_MS })).outcome).toBe("verified");
  });

  it("checks categories over the whole trace, not event by event", () => {
    // Event 2 is non-monotonic; event 5 is out of range. out_of_range runs first.
    const events: ShotEvent[] = [
      { tMs: 0, aimX: 0, power: 500 },
      { tMs: 2000, aimX: 0, power: 500 },
      { tMs: 1000, aimX: 0, power: 500 },
      { tMs: 3000, aimX: 0, power: 500 },
      { tMs: 4000, aimX: 1001, power: 500 },
    ];
    expect(evaluate(makeInput(events))).toEqual({ outcome: "rejected", reason: "out_of_range" });
  });

  it("reports too_many_events before cadence", () => {
    const events = Array.from({ length: 94 }, (_, i) => ({ tMs: i * 600, aimX: 0, power: 500 }));
    expect(evaluate(makeInput(events))).toEqual({ outcome: "rejected", reason: "too_many_events" });
  });

  it("treats equal timestamps as non_monotonic and a 649 ms gap as cadence_violation", () => {
    const equal = [
      { tMs: 1000, aimX: 0, power: 500 },
      { tMs: 1000, aimX: 0, power: 500 },
    ];
    const close = [
      { tMs: 1000, aimX: 0, power: 500 },
      { tMs: 1649, aimX: 0, power: 500 },
    ];
    expect(evaluate(makeInput(equal))).toEqual({ outcome: "rejected", reason: "non_monotonic" });
    expect(evaluate(makeInput(close))).toEqual({ outcome: "rejected", reason: "cadence_violation" });
  });

  it("rejects a trace that claims more time than has passed", () => {
    const events = [{ tMs: 59_800, aimX: 0, power: 500 }];
    const boundary = STARTED_AT_MS + 59_800 - CONFIG.earlyToleranceMs;
    expect(evaluate(makeInput(events, { submittedAtMs: boundary - 1 }))).toEqual({
      outcome: "rejected",
      reason: "submitted_too_early",
    });
    expect(evaluate(makeInput(events, { submittedAtMs: boundary })).outcome).toBe("verified");
  });

  it("uses maxT = 0 for an empty trace", () => {
    const result = evaluate(makeInput([], { submittedAtMs: STARTED_AT_MS - CONFIG.earlyToleranceMs }));
    expect(result.outcome).toBe("verified");
  });
});

describe("rulesetChecksum", () => {
  it("matches a known SHA-256 computed outside this code", async () => {
    // printf '%s' '{"a":2,"b":1}' | shasum -a 256
    expect(await rulesetChecksum({ b: 1, a: 2 })).toBe(
      "d3626ac30a87e6f7a6428233b3c68299976865fa5508e4267c5415c76af7a772",
    );
  });
});

describe("canonicalJson", () => {
  it("sorts keys and removes whitespace", () => {
    expect(canonicalJson({ b: 1, a: [2, { d: -3, c: "x" }] })).toBe('{"a":[2,{"c":"x","d":-3}],"b":1}');
  });

  it("rejects values outside the supported subset", () => {
    expect(() => canonicalJson({ a: 1.5 })).toThrow(TypeError);
    expect(() => canonicalJson({ a: null })).toThrow(TypeError);
    expect(() => canonicalJson({ a: 'quote"' })).toThrow(TypeError);
  });
});
