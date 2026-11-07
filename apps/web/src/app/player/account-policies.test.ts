import "@angular/compiler";
import { describe, expect, it } from "vitest";
import { parseBirthYear } from "./age-gate";
import { guestExpiry, LINK_PROMPT_DAYS } from "./guest-expiry";

describe("parseBirthYear", () => {
  it("accepts exactly 4 digits and gives no other hint", () => {
    expect(parseBirthYear(" 1990 ")).toBe(1990);
    expect(parseBirthYear("2015")).toBe(2015); // the API decides the age, not the page
    expect(parseBirthYear("")).toBeNull();
    expect(parseBirthYear("199")).toBeNull();
    expect(parseBirthYear("19900")).toBeNull();
    expect(parseBirthYear("19a0")).toBeNull();
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
