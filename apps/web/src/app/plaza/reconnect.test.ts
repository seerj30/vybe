import { describe, expect, it } from "vitest";
import { KICK_RULES, retryDelay, RETRY_LIMIT_MS, shutdownDelay } from "./reconnect";

const mid = () => 0.5; // no jitter
const low = () => 0; // −25 %
const high = () => 0.999_999; // about +25 %

describe("reconnect policy (protocol §8)", () => {
  it("waits 0, 2, 5, 10, 20, 30, 30 … s", () => {
    expect([0, 1, 2, 3, 4, 5, 6, 9].map((attempt) => retryDelay(attempt, 0, mid))).toEqual([0, 2000, 5000, 10000, 20000, 30000, 30000, 30000]);
  });

  it("adds ±25 % jitter", () => {
    expect(retryDelay(2, 0, low)).toBe(3750);
    expect(retryDelay(2, 0, high)).toBe(6250);
  });

  it("stops after 5 minutes, and the last try still starts inside them", () => {
    expect(retryDelay(8, RETRY_LIMIT_MS, mid)).toBeNull();
    expect(retryDelay(8, RETRY_LIMIT_MS - 10_000, mid)).toBe(9_999);
  });

  it("spreads the reconnect after a deploy over 2–6 s", () => {
    expect(shutdownDelay(low)).toBe(2000);
    expect(shutdownDelay(high)).toBe(6000);
  });

  it("follows the kick table", () => {
    expect(KICK_RULES.server_shutdown.action.kind).toBe("auto");
    expect(KICK_RULES.replaced.action.kind).toBe("none");
    expect(KICK_RULES.idle.action).toEqual({ kind: "manual", cooldownMs: 0 });
    expect(KICK_RULES.rate_limited.action).toEqual({ kind: "manual", cooldownMs: 60_000 });
    expect(KICK_RULES.invalid_input.action).toEqual({ kind: "manual", cooldownMs: 60_000 });
    expect(KICK_RULES.suspended.action.kind).toBe("none");
    expect(KICK_RULES.account_unavailable.action.kind).toBe("leave");
  });
});
