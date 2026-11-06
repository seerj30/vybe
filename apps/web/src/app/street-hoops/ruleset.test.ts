import { describe, expect, it } from "vitest";
import { applyOverrides, loadRuleset, verifyRuleset } from "./ruleset";

describe("ruleset", () => {
  it("loads the street-hoops-v1 fixture with a valid checksum", async () => {
    const ruleset = await loadRuleset();
    expect(ruleset.id).toBe("street-hoops-v1");
    expect(ruleset.config.durationMs).toBeGreaterThan(0);
  });

  it("rejects a config that does not match its checksum", async () => {
    const ruleset = await loadRuleset();
    const tampered = { ...ruleset, config: { ...ruleset.config, hitDx: ruleset.config.hitDx + 1 } };
    await expect(verifyRuleset(tampered)).rejects.toThrow(/checksum mismatch/);
  });

  it("applies valid overrides and keeps the other values", async () => {
    const { config } = await loadRuleset();
    const tuned = applyOverrides(config, { hitDx: 200, segmentMs: 3000 });
    expect(tuned).toEqual({ ...config, hitDx: 200, segmentMs: 3000 });
  });

  it("refuses overrides that break the simulator invariants", async () => {
    const { config } = await loadRuleset();
    expect(() => applyOverrides(config, { segmentMs: 7000 })).toThrow(RangeError);
    expect(() => applyOverrides(config, { xMin: 800 })).toThrow(RangeError);
    expect(() => applyOverrides(config, { minGapMs: 0 })).toThrow(RangeError);
    expect(() => applyOverrides(config, { hitDx: 1.5 })).toThrow(RangeError);
  });
});
