import { describe, expect, it } from "vitest";
import { aimFromFraction, chargePower, quantizeAim, quantizePower, stepAim } from "./input";

describe("quantizeAim", () => {
  it("rounds to integers and clamps to [-1000, 1000]", () => {
    expect(quantizeAim(12.4)).toBe(12);
    expect(quantizeAim(-12.6)).toBe(-13);
    expect(quantizeAim(1000.4)).toBe(1000);
    expect(quantizeAim(5000)).toBe(1000);
    expect(quantizeAim(-5000)).toBe(-1000);
  });

  it("rejects non-finite values", () => {
    expect(() => quantizeAim(Number.NaN)).toThrow(RangeError);
    expect(() => quantizeAim(Number.POSITIVE_INFINITY)).toThrow(RangeError);
  });
});

describe("quantizePower", () => {
  it("rounds to integers and clamps to [0, 1000]", () => {
    expect(quantizePower(499.5)).toBe(500);
    expect(quantizePower(-3)).toBe(0);
    expect(quantizePower(1200)).toBe(1000);
  });

  it("rejects non-finite values", () => {
    expect(() => quantizePower(Number.NaN)).toThrow(RangeError);
  });
});

describe("aimFromFraction", () => {
  it("maps the play area edges and centre", () => {
    expect(aimFromFraction(0)).toBe(-1000);
    expect(aimFromFraction(0.5)).toBe(0);
    expect(aimFromFraction(1)).toBe(1000);
    expect(aimFromFraction(0.25031)).toBe(-499);
  });

  it("clamps pointers outside the play area", () => {
    expect(aimFromFraction(-0.2)).toBe(-1000);
    expect(aimFromFraction(1.2)).toBe(1000);
  });
});

describe("stepAim", () => {
  it("moves by speed × time and stays an integer", () => {
    expect(stepAim(0, 1, 16.7, 1500)).toBe(25);
    expect(stepAim(0, -1, 33.3, 1500)).toBe(-50);
    expect(stepAim(10, 0, 100, 1500)).toBe(10);
  });

  it("clamps at the edges and ignores negative time", () => {
    expect(stepAim(990, 1, 100, 1500)).toBe(1000);
    expect(stepAim(0, 1, -100, 1500)).toBe(0);
  });
});

describe("chargePower", () => {
  it("rises to 1000 in one cycle, falls back in the next, and repeats", () => {
    expect(chargePower(0, 1000)).toBe(0);
    expect(chargePower(250, 1000)).toBe(250);
    expect(chargePower(1000, 1000)).toBe(1000);
    expect(chargePower(1500, 1000)).toBe(500);
    expect(chargePower(2000, 1000)).toBe(0);
    expect(chargePower(2300, 1000)).toBe(300);
  });

  it("quantizes to integers", () => {
    expect(chargePower(333.3, 1400)).toBe(238);
    expect(Number.isInteger(chargePower(17.77, 1234))).toBe(true);
  });

  it("rejects a non-positive cycle", () => {
    expect(() => chargePower(10, 0)).toThrow(RangeError);
  });
});
