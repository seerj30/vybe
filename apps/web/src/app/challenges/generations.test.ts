import { describe, expect, it } from "vitest";
import { Generations } from "./generations";

describe("Generations", () => {
  it("makes earlier work stale when new work starts", () => {
    const loads = new Generations();
    const first = loads.next();
    expect(first()).toBe(true);
    const second = loads.next();
    expect(first()).toBe(false);
    expect(second()).toBe(true);
  });

  it("lets an action check its screen without making the load stale", () => {
    const loads = new Generations();
    const load = loads.next();
    const action = loads.snapshot();
    expect(load()).toBe(true);
    expect(action()).toBe(true);
    loads.next();
    expect(action()).toBe(false);
  });

  it("drops a slow answer for an old route value (A then B, A answers last)", async () => {
    const loads = new Generations();
    const shown: string[] = [];
    const load = async (value: string, delayMs: number): Promise<void> => {
      const isCurrent = loads.next();
      await new Promise((resolve) => setTimeout(resolve, delayMs));
      if (isCurrent()) shown.push(value);
    };
    await Promise.all([load("a", 20), load("b", 1)]);
    expect(shown).toEqual(["b"]);
  });
});
