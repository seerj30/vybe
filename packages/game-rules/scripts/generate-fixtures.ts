/**
 * Regenerates the golden fixtures in packages/game-rules/fixtures from the TS
 * reference implementation. The C# implementation must reproduce every file.
 *
 * Usage: pnpm --filter @vybe/game-rules generate:fixtures
 * Review the git diff of fixtures/ before committing: a changed `expected`
 * value is a rules change and needs a new ruleset version.
 */
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { Pcg32 } from "../src/pcg32.ts";
import { rulesetChecksum } from "../src/checksum.ts";
import { evaluate, generateWaypoints, targetAt } from "../src/simulator.ts";
import { evaluateJuggle } from "../src/juggle.ts";
import { autoplay } from "../src/juggle-autoplay.ts";
import type { JuggleEvaluateInput, JuggleTouch, StreetJuggleConfig } from "../src/juggle-types.ts";
import type {
  EvaluateInput,
  EvaluateResult,
  RejectionReason,
  SessionStatus,
  ShotEvent,
  StreetHoopsConfig,
} from "../src/types.ts";

const FIXTURES = fileURLToPath(new URL("../fixtures/", import.meta.url));
const RULESET_PATH = `${FIXTURES}rulesets/street-hoops-v1.json`;

interface RulesetFile {
  id: string;
  gameId: string;
  version: number;
  config: StreetHoopsConfig;
  checksum: string;
}

type Dir = "scoring" | "rejections";
type Intent = "verified" | "expired" | RejectionReason;

const files = new Map<string, unknown>();

function toJson(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

const ruleset = JSON.parse(readFileSync(RULESET_PATH, "utf8")) as RulesetFile;
const config = ruleset.config;
files.set("rulesets/street-hoops-v1.json", { ...ruleset, checksum: await rulesetChecksum(config) });

const STARTED_AT_MS = 1_000;
const EPOCH_STARTED_AT_MS = 1_760_000_000_000;

// ---------------------------------------------------------------- PCG32 vectors
const pcgCases: Array<{ initState: string; initSeq: string; source: string }> = [
  { initState: "42", initSeq: "54", source: "official pcg32-demo output (pcg-random.org)" },
  { initState: "0", initSeq: "54", source: "generated" },
  { initState: "4294967295", initSeq: "54", source: "generated" },
  { initState: "4294967295", initSeq: "4294967295", source: "generated" },
  { initState: "12345", initSeq: "0", source: "generated" },
];
files.set("pcg32.json", {
  description: "PCG32 XSH-RR reference vectors. initState/initSeq are uint64 decimal strings.",
  vectors: pcgCases.map((c) => {
    const rng = new Pcg32(BigInt(c.initState), BigInt(c.initSeq));
    return { ...c, outputs: Array.from({ length: 10 }, () => rng.nextUint32()) };
  }),
});

// ---------------------------------------------------------------- helpers
interface FixtureOptions {
  seed?: number;
  status?: SessionStatus;
  startedAtMs?: number;
  /** Offset from startedAtMs; default: game end + 500 ms. */
  submitOffsetMs?: number;
  submittedRulesetId?: string;
  config?: StreetHoopsConfig;
}

function makeInput(events: ShotEvent[], o: FixtureOptions = {}): EvaluateInput {
  const cfg = o.config ?? config;
  const startedAtMs = o.startedAtMs ?? STARTED_AT_MS;
  return {
    config: cfg,
    rulesetId: ruleset.id,
    seed: o.seed ?? 12345,
    session: {
      status: o.status ?? "active",
      startedAtMs,
      submitDeadlineMs: startedAtMs + cfg.durationMs + cfg.graceMs,
    },
    submission: {
      rulesetId: o.submittedRulesetId ?? ruleset.id,
      submittedAtMs: startedAtMs + (o.submitOffsetMs ?? cfg.durationMs + 500),
      events,
    },
  };
}

const DEADLINE_OFFSET_MS = config.durationMs + config.graceMs;

/** Events at `times`, aimed at the target plus the given offsets. */
function aimedTrace(
  seed: number,
  times: readonly number[],
  offsets: ReadonlyArray<readonly [number, number]> = [],
  cfg: StreetHoopsConfig = config,
): ShotEvent[] {
  const waypoints = generateWaypoints(cfg, seed);
  return times.map((tMs, i) => {
    const { targetX, idealPower } = targetAt(cfg, waypoints, tMs);
    const [ddx, ddp] = offsets[i % Math.max(1, offsets.length)] ?? [0, 0];
    return { tMs, aimX: targetX + ddx, power: idealPower + ddp };
  });
}

function every(gapMs: number, count: number, startMs = 0): number[] {
  return Array.from({ length: count }, (_, i) => startMs + i * gapMs);
}

/** 31-bit LCG for varied but reproducible offsets (fixture data only, not a game rule). */
function offsetSequence(count: number): Array<[number, number]> {
  let s = 20261106;
  const next = (): number => {
    s = (Math.imul(s, 1103515245) + 12345) & 0x7fffffff;
    return s;
  };
  return Array.from({ length: count }, () => [(next() % 401) - 200, (next() % 251) - 150]);
}

function outcomeOf(result: EvaluateResult): Intent {
  return result.outcome === "rejected" ? result.reason : result.outcome;
}

function fixture(dir: Dir, name: string, description: string, intent: Intent, input: EvaluateInput): void {
  const expected = evaluate(input);
  if (outcomeOf(expected) !== intent) {
    throw new Error(`${dir}/${name}: expected ${intent}, got ${outcomeOf(expected)}`);
  }
  if (dir === "scoring" && expected.outcome !== "verified") {
    throw new Error(`${dir}/${name}: scoring fixtures must be verified`);
  }
  files.set(`${dir}/${name}.json`, {
    name,
    description,
    waypoints: generateWaypoints(input.config, input.seed),
    input,
    expected,
  });
}

// ---------------------------------------------------------------- scoring
const irregularTimes = [0, 650, 3999, 7999, 12000, 31337, 47999, 52000, 59999];
const irregularOffsets: Array<[number, number]> = [[0, 0], [30, -40], [-120, 0], [0, -100], [170, 0]];

fixture("scoring", "all-perfect-max-score", "93 perfect shots every 650 ms: maximum score 22,900.", "verified",
  makeInput(aimedTrace(12345, every(650, 93)), { seed: 12345 }));

fixture("scoring", "threshold-boundaries", "dx/dp exactly on and one past each threshold.", "verified",
  makeInput(
    aimedTrace(7, every(1000, 10), [
      [50, 0], [51, 0], [0, -50], [0, -51], [160, 0], [161, 0], [0, -120], [0, -121], [-50, -50], [-160, -120],
    ]),
    { seed: 7 },
  ));

fixture("scoring", "streak-and-reset", "Streak grows to the +100 cap, resets on a miss, grows again.", "verified",
  makeInput(
    aimedTrace(2026, every(4100, 14), [
      [0, 0], [0, 0], [0, 0], [0, 0], [0, 0], [0, 0], [400, 0], [100, 0], [100, 0], [100, 0], [0, -300], [0, 0], [0, 0], [0, 0],
    ]),
    { seed: 2026 },
  ));

fixture("scoring", "empty-trace", "No shots: verified with score 0.", "verified", makeInput([], { seed: 1 }));

fixture("scoring", "seed-zero", "Seed 0, irregular times incl. segment edges and t = 59,999.", "verified",
  makeInput(aimedTrace(0, irregularTimes, irregularOffsets), { seed: 0 }));

fixture("scoring", "seed-max-uint32", "Seed 4,294,967,295 (uint32 max), same times as seed-zero.", "verified",
  makeInput(aimedTrace(0xffffffff, irregularTimes, irregularOffsets), { seed: 0xffffffff }));

const altStreamConfig: StreetHoopsConfig = { ...config, streamId: 0xffffffff };
fixture("scoring", "stream-id-max-uint32", "Same rules with streamId 4,294,967,295.", "verified",
  makeInput(aimedTrace(12345, irregularTimes, irregularOffsets, altStreamConfig), {
    seed: 12345,
    config: altStreamConfig,
  }));

fixture("scoring", "epoch-ms-times", "Production-size epoch-millisecond times (above int32).", "verified",
  makeInput(aimedTrace(31, every(2000, 30)), { seed: 31, startedAtMs: EPOCH_STARTED_AT_MS }));

fixture("scoring", "mixed-random-offsets", "40 shots every 1,400 ms with varied offsets.", "verified",
  makeInput(aimedTrace(99, every(1400, 40), offsetSequence(40)), { seed: 99 }));

fixture("scoring", "submit-at-deadline", "Submitted exactly at submitDeadlineMs: still verified.", "verified",
  makeInput(aimedTrace(5, every(5000, 12)), { seed: 5, submitOffsetMs: DEADLINE_OFFSET_MS }));

fixture("scoring", "submit-early-boundary", "Submitted exactly at startedAt + maxT - earlyTolerance: verified.",
  "verified", makeInput(aimedTrace(5, [59_800]), { seed: 5, submitOffsetMs: 59_800 - config.earlyToleranceMs }));

// ---------------------------------------------------------------- rejections
const valid = aimedTrace(12345, every(1000, 5));
const neutral = (tMs: number): ShotEvent => ({ tMs, aimX: 0, power: 500 });

fixture("rejections", "not-started", "Session still created: rejected not_started.", "not_started",
  makeInput(valid, { status: "created" }));
fixture("rejections", "expired-status", "Session already expired: outcome expired.", "expired",
  makeInput(valid, { status: "expired" }));
fixture("rejections", "late-submit", "One ms after submitDeadlineMs: outcome expired.", "expired",
  makeInput(valid, { submitOffsetMs: DEADLINE_OFFSET_MS + 1 }));
fixture("rejections", "late-submit-epoch-ms", "Late submit with epoch-millisecond times.", "expired",
  makeInput(valid, { startedAtMs: EPOCH_STARTED_AT_MS, submitOffsetMs: DEADLINE_OFFSET_MS + 1 }));
fixture("rejections", "late-beats-ruleset-mismatch", "Expiry is checked before the ruleset id.", "expired",
  makeInput(valid, { submitOffsetMs: DEADLINE_OFFSET_MS + 1, submittedRulesetId: "street-hoops-v0" }));
fixture("rejections", "ruleset-mismatch", "Submission names another ruleset.", "ruleset_mismatch",
  makeInput(valid, { submittedRulesetId: "street-hoops-v0" }));

const outOfRange: Array<[string, ShotEvent]> = [
  ["aimx-high", { tMs: 2000, aimX: 1001, power: 500 }],
  ["aimx-low", { tMs: 2000, aimX: -1001, power: 500 }],
  ["power-high", { tMs: 2000, aimX: 0, power: 1001 }],
  ["power-low", { tMs: 2000, aimX: 0, power: -1 }],
  ["tms-negative", { tMs: -1, aimX: 0, power: 500 }],
  ["tms-end", { tMs: config.durationMs, aimX: 0, power: 500 }],
];
for (const [suffix, event] of outOfRange) {
  fixture("rejections", `out-of-range-${suffix}`, `One field outside its range (${suffix}).`, "out_of_range",
    makeInput([neutral(500), event].sort((a, b) => a.tMs - b.tMs)));
}

fixture("rejections", "category-order", "Event 2 is non-monotonic and event 4 is out of range: out_of_range wins.",
  "out_of_range",
  makeInput([neutral(0), neutral(2000), neutral(1000), neutral(3000), { tMs: 4000, aimX: 1001, power: 500 }]));
fixture("rejections", "too-many-events", "94 events (600 ms gap): too_many_events before cadence.",
  "too_many_events", makeInput(every(600, 94).map(neutral)));
fixture("rejections", "non-monotonic-equal", "Two events with the same tMs.", "non_monotonic",
  makeInput([neutral(1000), neutral(1000)]));
fixture("rejections", "non-monotonic-decreasing", "tMs goes backwards.", "non_monotonic",
  makeInput([neutral(5000), neutral(4000)]));
fixture("rejections", "cadence-violation", "Gap of 649 ms.", "cadence_violation",
  makeInput([neutral(1000), neutral(1649)]));
fixture("rejections", "submitted-too-early", "Trace claims more game time than has passed.", "submitted_too_early",
  makeInput([neutral(59_800)], { submitOffsetMs: 59_800 - config.earlyToleranceMs - 1 }));


// ================================================================ Street Juggle (docs/design/p1-second-game.md)
const JUGGLE_RULESET_PATH = `${FIXTURES}rulesets/street-juggle-v1.json`;

interface JuggleRulesetFile {
  id: string;
  gameId: string;
  version: number;
  config: StreetJuggleConfig;
  checksum: string;
}

const juggleRuleset = JSON.parse(readFileSync(JUGGLE_RULESET_PATH, "utf8")) as JuggleRulesetFile;
const juggleConfig = juggleRuleset.config;
files.set("rulesets/street-juggle-v1.json", { ...juggleRuleset, checksum: await rulesetChecksum(juggleConfig) });

interface JuggleOptions {
  seed?: number;
  status?: SessionStatus;
  startedAtMs?: number;
  /** Offset from startedAtMs; default: game end + 500 ms. */
  submitOffsetMs?: number;
  submittedRulesetId?: string;
  config?: StreetJuggleConfig;
}

function juggleInput(events: JuggleTouch[], o: JuggleOptions = {}): JuggleEvaluateInput {
  const cfg = o.config ?? juggleConfig;
  const startedAtMs = o.startedAtMs ?? STARTED_AT_MS;
  return {
    config: cfg,
    rulesetId: juggleRuleset.id,
    seed: o.seed ?? 12345,
    session: {
      status: o.status ?? "active",
      startedAtMs,
      submitDeadlineMs: startedAtMs + cfg.durationMs + cfg.graceMs,
    },
    submission: {
      rulesetId: o.submittedRulesetId ?? juggleRuleset.id,
      submittedAtMs: startedAtMs + (o.submitOffsetMs ?? cfg.durationMs + 500),
      events,
    },
  };
}

const JUGGLE_DEADLINE_OFFSET_MS = juggleConfig.durationMs + juggleConfig.graceMs;

/** Timing errors in ms with a fixed 31-bit LCG (fixture data only, not a game rule). */
function timingErrors(count: number, spreadMs: number, start: number): number[] {
  let s = start;
  return Array.from({ length: count }, () => {
    s = (Math.imul(s, 1103515245) + 12345) & 0x7fffffff;
    return (s % (2 * spreadMs + 1)) - spreadMs;
  });
}

function juggleFixture(dir: Dir, name: string, description: string, intent: Intent, input: JuggleEvaluateInput,
  check: (expected: Extract<ReturnType<typeof evaluateJuggle>, { outcome: "verified" }>) => boolean = () => true): void {
  const expected = evaluateJuggle(input);
  const got = expected.outcome === "rejected" ? expected.reason : expected.outcome;
  if (got !== intent) throw new Error(`street-juggle/${dir}/${name}: expected ${intent}, got ${got}`);
  if (dir === "scoring" && (expected.outcome !== "verified" || !check(expected))) {
    throw new Error(`street-juggle/${dir}/${name}: the fixture does not show what its description says`);
  }
  files.set(`street-juggle/${dir}/${name}.json`, { name, description, input, expected });
}

const cfgJ = juggleConfig;
const bot = (seed: number, cfg: StreetJuggleConfig = cfgJ, o: Parameters<typeof autoplay>[2] = {}): JuggleTouch[] =>
  autoplay(cfg, seed, o);

// ---------------------------------------------------------------- juggle scoring
juggleFixture("scoring", "bot-perfect", "A scripted player touches every ball at the ideal tick.", "verified",
  juggleInput(bot(12345), { seed: 12345 }),
  (e) => e.dropCount === 0 && e.perfectCount === e.kickCount && e.touches.some((t) => t.points > cfgJ.perfectPoints + cfgJ.streakMax));
juggleFixture("scoring", "timing-error-30ms", "Timing errors up to ±60 ms: perfects and goods.", "verified",
  juggleInput(bot(99, cfgJ, { offsetsMs: timingErrors(400, 60, 7) }), { seed: 99 }),
  (e) => e.perfectCount > 0 && e.kickCount > e.perfectCount);
juggleFixture("scoring", "timing-error-300ms", "Timing errors up to ±300 ms: whiffs, drops and new serves.", "verified",
  juggleInput(bot(2026, cfgJ, { offsetsMs: timingErrors(400, 300, 11) }), { seed: 2026 }),
  (e) => e.dropCount > 0 && e.touches.some((t) => t.result === "whiff"));
juggleFixture("scoring", "wrong-spot-lock", "Every fifth touch goes to a wrong spot: whiff, streak reset and lock.", "verified",
  juggleInput(bot(31, cfgJ, { wrongLane: (i) => i % 5 === 4 }), { seed: 31 }),
  (e) => e.touches.some((t) => t.result === "whiff" && t.ballLane !== t.lane && t.ballLane >= 0));
juggleFixture("scoring", "empty-trace", "No touches: every ball drops; verified with score 0.", "verified",
  juggleInput([], { seed: 1 }), (e) => e.score === 0 && e.dropCount > 20);
juggleFixture("scoring", "spam-all-spots", "400 touches every 150 ms, cycling the spots: the lock makes spam pay little.", "verified",
  juggleInput(every(150, 400).map((tMs, i) => ({ tMs, lane: i % 3 })), { seed: 5 }),
  (e) => e.eventCount === 400 && e.touches.filter((t) => t.result === "whiff").length > 300);
juggleFixture("scoring", "touch-while-waiting", "Touches while the ball waits for its serve are whiffs.", "verified",
  juggleInput([{ tMs: 0, lane: 1 }, { tMs: 2_500, lane: 0 }, { tMs: 3_400, lane: 2 }, { tMs: 3_550, lane: 1 }], { seed: 8 }),
  (e) => e.touches.some((t) => t.ballLane === -1 && t.result === "whiff"));
juggleFixture("scoring", "seed-zero", "Seed 0 with the scripted player.", "verified", juggleInput(bot(0), { seed: 0 }));
juggleFixture("scoring", "seed-max-uint32", "Seed 4,294,967,295 (uint32 max).", "verified",
  juggleInput(bot(0xffffffff, cfgJ, { offsetsMs: timingErrors(400, 40, 3) }), { seed: 0xffffffff }));
const altJuggleStream: StreetJuggleConfig = { ...cfgJ, streamId: 0xffffffff };
juggleFixture("scoring", "stream-id-max-uint32", "Same rules with streamId 4,294,967,295.", "verified",
  juggleInput(bot(12345, altJuggleStream), { seed: 12345, config: altJuggleStream }));
const oneSpot: StreetJuggleConfig = { ...cfgJ, lanes: 1, respawnMs: 0, whiffLockMs: 0, starPercent: 100, levelMs: 6_000 };
juggleFixture("scoring", "one-spot-edge-config", "Edge config: one spot, no lock, no respawn pause, every ball gold, 10 levels.", "verified",
  juggleInput(bot(77, oneSpot, { offsetsMs: timingErrors(400, 400, 5) }), { seed: 77, config: oneSpot }),
  (e) => e.kickCount > 0 && e.dropCount > 0);
juggleFixture("scoring", "epoch-ms-times", "Production-size epoch-millisecond times (above int32).", "verified",
  juggleInput(bot(31), { seed: 31, startedAtMs: EPOCH_STARTED_AT_MS }));
juggleFixture("scoring", "submit-at-deadline", "Submitted exactly at submitDeadlineMs: still verified.", "verified",
  juggleInput(bot(5), { seed: 5, submitOffsetMs: JUGGLE_DEADLINE_OFFSET_MS }));
juggleFixture("scoring", "submit-early-boundary", "Submitted exactly at startedAt + maxT - earlyTolerance: verified.", "verified",
  juggleInput([{ tMs: 59_990, lane: 1 }], { seed: 5, submitOffsetMs: 59_990 - cfgJ.earlyToleranceMs }));

// ---------------------------------------------------------------- juggle rejections
const jValid = bot(12345, cfgJ, { maxTouches: 6 });
const spot = (tMs: number, lane = 1): JuggleTouch => ({ tMs, lane });

juggleFixture("rejections", "not-started", "Session still created: rejected not_started.", "not_started",
  juggleInput(jValid, { status: "created" }));
juggleFixture("rejections", "expired-status", "Session already expired: outcome expired.", "expired",
  juggleInput(jValid, { status: "expired" }));
juggleFixture("rejections", "late-submit", "One ms after submitDeadlineMs: outcome expired.", "expired",
  juggleInput(jValid, { submitOffsetMs: JUGGLE_DEADLINE_OFFSET_MS + 1 }));
juggleFixture("rejections", "late-beats-ruleset-mismatch", "Expiry is checked before the ruleset id.", "expired",
  juggleInput(jValid, { submitOffsetMs: JUGGLE_DEADLINE_OFFSET_MS + 1, submittedRulesetId: "street-hoops-v1" }));
juggleFixture("rejections", "ruleset-mismatch", "Submission names another game's ruleset.", "ruleset_mismatch",
  juggleInput(jValid, { submittedRulesetId: "street-hoops-v1" }));
const juggleOutOfRange: Array<[string, JuggleTouch]> = [
  ["lane-high", { tMs: 2000, lane: 3 }],
  ["lane-low", { tMs: 2000, lane: -1 }],
  ["tms-negative", { tMs: -1, lane: 1 }],
  ["tms-end", { tMs: cfgJ.durationMs, lane: 1 }],
];
for (const [suffix, event] of juggleOutOfRange) {
  juggleFixture("rejections", `out-of-range-${suffix}`, `One field outside its range (${suffix}).`, "out_of_range",
    juggleInput([spot(500), event].sort((a, b) => a.tMs - b.tMs)));
}
juggleFixture("rejections", "category-order", "Event 2 is non-monotonic and event 4 is out of range: out_of_range wins.",
  "out_of_range", juggleInput([spot(0), spot(2000), spot(1000), spot(3000), { tMs: 4000, lane: 3 }]));
juggleFixture("rejections", "too-many-events", "401 events (140 ms gap): too_many_events before cadence.",
  "too_many_events", juggleInput(every(140, 401).map((t) => spot(t))));
juggleFixture("rejections", "non-monotonic-equal", "Two events with the same tMs.", "non_monotonic",
  juggleInput([spot(1000), spot(1000, 2)]));
juggleFixture("rejections", "non-monotonic-decreasing", "tMs goes backwards.", "non_monotonic",
  juggleInput([spot(5000), spot(4000)]));
juggleFixture("rejections", "cadence-violation", "Gap of 149 ms.", "cadence_violation",
  juggleInput([spot(1000), spot(1149, 0)]));
juggleFixture("rejections", "submitted-too-early", "Trace claims more game time than has passed.", "submitted_too_early",
  juggleInput([spot(59_800)], { submitOffsetMs: 59_800 - cfgJ.earlyToleranceMs - 1 }));

// ---------------------------------------------------------------- write (only after every fixture succeeded)
for (const dir of ["scoring", "rejections", "street-juggle/scoring", "street-juggle/rejections"]) {
  rmSync(`${FIXTURES}${dir}`, { recursive: true, force: true });
  mkdirSync(`${FIXTURES}${dir}`, { recursive: true });
}
for (const [path, value] of files) {
  writeFileSync(`${FIXTURES}${path}`, toJson(value));
}

process.stdout.write(`fixtures regenerated: ${files.size} files\n`);
