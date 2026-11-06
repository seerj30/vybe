import { readdirSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { Pcg32 } from "../src/pcg32.ts";
import { rulesetChecksum } from "../src/checksum.ts";
import { evaluate, generateWaypoints } from "../src/simulator.ts";
import type { EvaluateInput, EvaluateResult, Waypoint } from "../src/types.ts";
import { FIXTURES_DIR, readJson, RULESET } from "./helpers.ts";

interface Fixture {
  name: string;
  waypoints: Waypoint[];
  input: EvaluateInput;
  expected: EvaluateResult;
}

interface PcgFile {
  vectors: Array<{ initState: string; initSeq: string; outputs: number[] }>;
}

function fixtureNames(dir: string): string[] {
  return readdirSync(`${FIXTURES_DIR}${dir}`).filter((f) => f.endsWith(".json")).sort();
}

describe("golden fixtures", () => {
  it("matches every PCG32 vector", () => {
    for (const v of readJson<PcgFile>("pcg32.json").vectors) {
      const rng = new Pcg32(BigInt(v.initState), BigInt(v.initSeq));
      expect(v.outputs.map(() => rng.nextUint32())).toEqual(v.outputs);
    }
  });

  it("matches the ruleset checksum", async () => {
    expect(RULESET.checksum).toMatch(/^[0-9a-f]{64}$/);
    expect(await rulesetChecksum(RULESET.config)).toBe(RULESET.checksum);
  });

  for (const dir of ["scoring", "rejections"]) {
    const names = fixtureNames(dir);

    it(`has ${dir} fixtures`, () => {
      expect(names.length).toBeGreaterThan(0);
    });

    for (const file of names) {
      it(`${dir}/${file}`, () => {
        const fixture = readJson<Fixture>(`${dir}/${file}`);
        expect(generateWaypoints(fixture.input.config, fixture.input.seed)).toEqual(fixture.waypoints);
        expect(evaluate(fixture.input)).toEqual(fixture.expected);
      });
    }
  }
});
