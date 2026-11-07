import { describe, expect, it } from "vitest";
import { type Challenge, inviteToken, opponentOf, outcomeFor, sideOf } from "./challenge-view";

const creator = { handle: "ana", displayName: "Ana", level: 3 };
const recipient = { handle: "bo", displayName: "Bo", level: 2 };

function challenge(overrides: Partial<Challenge> = {}): Challenge {
  return {
    challengeId: "c1",
    status: "completed",
    gameId: "street-hoops",
    rulesetId: "street-hoops-v1",
    creator,
    recipient,
    creatorScore: 900,
    recipientScore: 1200,
    result: "recipient_win",
    createdAt: "2026-11-06T10:00:00Z",
    acceptDeadline: "2026-11-07T10:00:00Z",
    acceptedAt: "2026-11-06T11:00:00Z",
    completeDeadline: "2026-11-07T11:00:00Z",
    resolvedAt: "2026-11-06T11:02:00Z",
    rematchOfChallengeId: null,
    shareUrl: null,
    ...overrides,
  };
}

describe("inviteToken", () => {
  it("reads the token of a share link on any origin", () => {
    expect(inviteToken("https://vybe.invalid/challenge/abc_DEF-123")).toBe("abc_DEF-123");
  });

  it("refuses other forms", () => {
    expect(inviteToken("not a url")).toBeNull();
    expect(inviteToken("https://vybe.invalid/other/abc")).toBeNull();
    expect(inviteToken("https://vybe.invalid/challenge/a/b")).toBeNull();
  });
});

describe("challenge sides and outcomes", () => {
  it("finds the caller's side and opponent", () => {
    expect(sideOf(challenge(), "ana")).toBe("creator");
    expect(sideOf(challenge(), "bo")).toBe("recipient");
    expect(opponentOf(challenge(), "creator")).toEqual(recipient);
    expect(opponentOf(challenge({ recipient: null, status: "open" }), "creator")).toBeNull();
  });

  it("gives each side its result", () => {
    expect(outcomeFor(challenge(), "recipient")).toBe("won");
    expect(outcomeFor(challenge(), "creator")).toBe("lost");
    expect(outcomeFor(challenge({ result: "creator_win" }), "creator")).toBe("won");
    expect(outcomeFor(challenge({ result: "tie" }), "creator")).toBe("tie");
    expect(outcomeFor(challenge({ result: null, status: "accepted" }), "creator")).toBeNull();
  });
});
