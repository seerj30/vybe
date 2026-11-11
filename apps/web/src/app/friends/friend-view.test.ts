import { describe, expect, it } from "vitest";
import { ApiError } from "../api/api-client";
import { friendErrorMessage, isHandleInput, isoWeek, previousWeek, requestSentText, weekRange } from "./friend-view";

describe("friend view helpers", () => {
  it("accepts only contract handles", () => {
    expect(isHandleInput("Bo_42")).toBe(true);
    expect(isHandleInput("ab")).toBe(false);
    expect(isHandleInput("a-b-c")).toBe(false);
    expect(isHandleInput("x".repeat(21))).toBe(false);
  });

  it("tells a sent request from an instant friendship", () => {
    expect(requestSentText({ handle: "bo", status: "pending" })).toBe("Friend request sent to bo.");
    expect(requestSentText({ handle: "bo", status: "accepted" })).toBe("You and bo are now friends.");
  });

  it("gives a friend text for recipient_blocked", () => {
    const blocked = new ApiError(422, "recipient_blocked");
    expect(friendErrorMessage(blocked)).toContain("friend request");
    expect(friendErrorMessage(new ApiError(429, "rate_limited"))).toBe("Too many attempts. Wait a minute and try again.");
  });

  it("computes ISO weeks in UTC", () => {
    expect(isoWeek(new Date("2026-11-15T23:59:59Z"))).toBe("2026-W46");
    expect(isoWeek(new Date("2026-11-16T00:00:00Z"))).toBe("2026-W47");
    expect(isoWeek(new Date("2026-01-01T12:00:00Z"))).toBe("2026-W01");
    expect(isoWeek(new Date("2026-12-31T12:00:00Z"))).toBe("2026-W53");
    expect(previousWeek("2026-11-09")).toBe("2026-W45");
    expect(previousWeek("2026-01-05")).toBe("2026-W01");
  });

  it("formats the week range", () => {
    expect(weekRange("2026-11-09", "2026-11-15")).toBe("9 Nov – 15 Nov 2026");
  });
});
