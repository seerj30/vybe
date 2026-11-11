import type { Schemas } from "@vybe/contracts";
import { describe, expect, it, vi } from "vitest";
import fixture from "../../../../../packages/game-rules/fixtures/rulesets/street-hoops-v1.json";
import type { ApiClient } from "../api/api-client";
import { isRewardEligible, noCoinsText, OnlineSessions, rejectionText, sessionOutcome } from "./online-session";
import type { RulesetFile } from "./ruleset";

const SESSION_ID = "0190a000-0000-7000-8000-000000000001";
const REWARDS: Schemas["Rewards"] = { coins: 30, xp: 15, levelAfter: 1, noCoinsReason: null };

function session(overrides: Partial<Schemas["GameSession"]>): Schemas["GameSession"] {
  return {
    sessionId: SESSION_ID,
    gameId: "street-hoops",
    rulesetId: fixture.id,
    status: "active",
    challengeId: null,
    createdAt: "t",
    startDeadline: "t",
    startedAt: "t",
    submitDeadline: "t",
    score: null,
    rewards: null,
    rejectionReason: null,
    ...overrides,
  };
}

function fakeApi(overrides: Partial<Record<keyof ApiClient, unknown>> = {}): ApiClient {
  return {
    createStreetHoopsSession: vi.fn(async () => ({
      sessionId: SESSION_ID,
      status: "created",
      rulesetId: fixture.id,
      rulesetChecksum: fixture.checksum,
      startDeadline: "t",
    })),
    getRuleset: vi.fn(async () => ({ ...fixture })),
    startSession: vi.fn(async () => ({ seed: 42, startedAt: "t", submitDeadline: "t" })),
    submitSession: vi.fn(async () => ({ sessionId: SESSION_ID, status: "verified", score: 100, rewards: REWARDS })),
    getSession: vi.fn(async () => session({ status: "verified", score: 100, rewards: REWARDS })),
    ...overrides,
  } as unknown as ApiClient;
}

const passThrough = (file: RulesetFile): Promise<RulesetFile> => Promise.resolve(file);
const noSleep = (): Promise<void> => Promise.resolve();

describe("OnlineSessions", () => {
  it("creates, verifies the ruleset, starts and returns the seed with a fresh submit key", async () => {
    const api = fakeApi();
    const verify = vi.fn(passThrough);
    const run = await new OnlineSessions(api, verify).prepare();

    expect(run).toMatchObject({ sessionId: SESSION_ID, seed: 42, ruleset: { id: fixture.id, checksum: fixture.checksum } });
    expect(run.submitKey).toMatch(/^[0-9a-f-]{36}$/);
    expect(verify).toHaveBeenCalledOnce();
    expect(api.startSession).toHaveBeenCalledWith(SESSION_ID);
  });

  it("creates the recipient session of a challenge with its id, and a plain session without", async () => {
    const api = fakeApi();
    const sessions = new OnlineSessions(api, passThrough);
    await sessions.prepare("c1");
    await sessions.prepare();
    expect(api.createStreetHoopsSession).toHaveBeenNthCalledWith(1, { challengeId: "c1" });
    expect(api.createStreetHoopsSession).toHaveBeenNthCalledWith(2, {});
  });

  it("creates the session through the game's own call", async () => {
    const api = fakeApi({
      createStreetJuggleSession: vi.fn(async () => ({
        sessionId: SESSION_ID,
        status: "created",
        rulesetId: fixture.id,
        rulesetChecksum: fixture.checksum,
        startDeadline: "t",
      })),
    });
    const game = { gameId: "street-hoops" as const, create: (client: ApiClient) => client.createStreetJuggleSession({}) };
    await new OnlineSessions(api, passThrough, noSleep, game).prepare();
    expect(api.createStreetJuggleSession).toHaveBeenCalledOnce();
    expect(api.createStreetHoopsSession).not.toHaveBeenCalled();
  });

  it("refuses a ruleset of another game before it starts the clock", async () => {
    const api = fakeApi({ getRuleset: vi.fn(async () => ({ ...fixture, gameId: "street-juggle" })) });
    await expect(new OnlineSessions(api, passThrough).prepare()).rejects.toThrow(/is for street-juggle, not street-hoops/);
    expect(api.startSession).not.toHaveBeenCalled();
  });

  it("downloads and verifies a ruleset once per id", async () => {
    const api = fakeApi();
    const sessions = new OnlineSessions(api, passThrough);
    await sessions.prepare();
    await sessions.prepare();
    expect(api.getRuleset).toHaveBeenCalledOnce();
  });

  it("refuses a ruleset whose checksum differs from the session's, before it starts the clock", async () => {
    const api = fakeApi({ getRuleset: vi.fn(async () => ({ ...fixture, checksum: "0".repeat(64) })) });
    await expect(new OnlineSessions(api, passThrough).prepare()).rejects.toThrow(/checksum/);
    expect(api.startSession).not.toHaveBeenCalled();
  });

  it("sends the trace with the run's key and returns the verified outcome", async () => {
    const api = fakeApi();
    const sessions = new OnlineSessions(api, passThrough);
    const run = await sessions.prepare();
    const events = [{ tMs: 1000, aimX: 0, power: 500 }];

    await expect(sessions.submit(run, events, 100)).resolves.toEqual({ status: "verified", score: 100, rewards: REWARDS });
    expect(api.submitSession).toHaveBeenCalledWith(SESSION_ID, { rulesetId: fixture.id, events, clientReportedScore: 100 }, run.submitKey);
  });

  it("maps rejected and expired results", async () => {
    const rejected = fakeApi({ submitSession: vi.fn(async () => ({ sessionId: SESSION_ID, status: "rejected", rejectionReason: "cadence_violation" })) });
    const sessions = new OnlineSessions(rejected, passThrough);
    const run = await sessions.prepare();
    await expect(sessions.submit(run, [], 0)).resolves.toEqual({ status: "rejected", reason: "cadence_violation" });

    const expired = fakeApi({ submitSession: vi.fn(async () => ({ sessionId: SESSION_ID, status: "expired" })) });
    const sessions2 = new OnlineSessions(expired, passThrough);
    await expect(sessions2.submit(await sessions2.prepare(), [], 0)).resolves.toEqual({ status: "expired" });
  });

  it("polls the session after 202 until the outcome is final", async () => {
    const getSession = vi
      .fn()
      .mockResolvedValueOnce(session({ status: "submitted" }))
      .mockResolvedValueOnce(session({ status: "verified", score: 100, rewards: REWARDS }));
    const api = fakeApi({ submitSession: vi.fn(async () => ({ sessionId: SESSION_ID, status: "submitted" })), getSession });
    const sessions = new OnlineSessions(api, passThrough, noSleep);

    await expect(sessions.submit(await sessions.prepare(), [], 0)).resolves.toMatchObject({ status: "verified" });
    expect(getSession).toHaveBeenCalledTimes(2);
  });
});

describe("sessionOutcome", () => {
  it("is null while the session is not final", () => {
    expect(sessionOutcome(session({ status: "active" }))).toBeNull();
    expect(sessionOutcome(session({ status: "submitted" }))).toBeNull();
  });

  it("returns the stored outcome of a final session", () => {
    expect(sessionOutcome(session({ status: "verified", score: 7, rewards: REWARDS }))).toEqual({ status: "verified", score: 7, rewards: REWARDS });
    expect(sessionOutcome(session({ status: "rejected", rejectionReason: "out_of_range" }))).toEqual({ status: "rejected", reason: "out_of_range" });
    expect(sessionOutcome(session({ status: "expired" }))).toEqual({ status: "expired" });
  });
});

describe("rejectionText", () => {
  it("explains a reason in words", () => {
    expect(rejectionText("submitted_too_early")).toBe("it claimed more game time than had passed");
  });
});

describe("noCoinsText", () => {
  it("names each reason with the ruleset numbers", () => {
    expect(noCoinsText("run_too_short", fixture.config)).toMatch(/^No coins: run too short\./);
    expect(noCoinsText("score_below_minimum", fixture.config)).toMatch(/^No coins: score below 500\. Your XP and quest progress still count\./);
    expect(noCoinsText("too_few_shots", fixture.config)).toContain("at least 5 shots");
    expect(noCoinsText("daily_limit", fixture.config)).toContain("00:00 UTC");
  });
});

describe("isRewardEligible", () => {
  it("is false only for runs that are not reward-eligible", () => {
    const rewardsWith = (noCoinsReason: Schemas["Rewards"]["noCoinsReason"]) => isRewardEligible({ ...REWARDS, coins: 0, noCoinsReason });
    expect(rewardsWith("too_few_shots")).toBe(false);
    expect(rewardsWith("run_too_short")).toBe(false);
    expect(rewardsWith("score_below_minimum")).toBe(true);
    expect(rewardsWith("daily_limit")).toBe(true);
    expect(isRewardEligible(REWARDS)).toBe(true);
  });
});
