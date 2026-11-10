import "@angular/compiler";
import { describe, expect, it } from "vitest";
import { parseBirthDate } from "./age-gate";
import { guestExpiry, LINK_PROMPT_DAYS } from "./guest-expiry";

describe("parseBirthDate", () => {
  it("needs a month and a 4-digit year, and gives no other hint", () => {
    expect(parseBirthDate("6", " 1990 ")).toEqual({ birthYear: 1990, birthMonth: 6 });
    expect(parseBirthDate("12", "2020")).toEqual({ birthYear: 2020, birthMonth: 12 }); // the API decides the age
    expect(parseBirthDate("", "1990")).toBeNull();
    expect(parseBirthDate("13", "1990")).toBeNull();
    expect(parseBirthDate("6", "199")).toBeNull();
    expect(parseBirthDate("6", "19a0")).toBeNull();
  });
});

describe("guestExpiry", () => {
  const now = new Date("2026-11-07T12:00:00Z");
  const days = (n: number): string => new Date(now.getTime() + n * 86_400_000).toISOString();

  it("is null for registered players", () => {
    expect(guestExpiry({ kind: "registered", guestExpiresAt: null }, now)).toBeNull();
  });

  it("shows the link prompt only when fewer than 14 days remain", () => {
    expect(guestExpiry({ kind: "guest", guestExpiresAt: days(30) }, now)).toMatchObject({ daysLeft: 30, prompt: false });
    expect(guestExpiry({ kind: "guest", guestExpiresAt: days(LINK_PROMPT_DAYS) }, now)?.prompt).toBe(false);
    expect(guestExpiry({ kind: "guest", guestExpiresAt: days(13.5) }, now)).toMatchObject({ daysLeft: 13, prompt: true });
    expect(guestExpiry({ kind: "guest", guestExpiresAt: days(0.25) }, now)).toMatchObject({ daysLeft: 0, prompt: true });
  });
});
