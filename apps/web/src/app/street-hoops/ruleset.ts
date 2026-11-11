import { assertValidConfig, rulesetChecksum, type StreetHoopsConfig } from "@vybe/game-rules";
import fixture from "../../../../../packages/game-rules/fixtures/rulesets/street-hoops-v1.json";

export interface RulesetFile<C = StreetHoopsConfig> {
  id: string;
  config: C;
  checksum: string;
}

/** Config keys the dev tuning panel can override. */
export const TUNABLE_KEYS = [
  "hitDx",
  "hitDp",
  "perfectDx",
  "perfectDp",
  "segmentMs",
  "xMin",
  "xMax",
  "pMin",
  "pMax",
  "minGapMs",
] as const satisfies readonly (keyof StreetHoopsConfig)[];

export type TunableKey = (typeof TUNABLE_KEYS)[number];
export type Overrides = Partial<Record<TunableKey, number>>;

/** Checks the checksum (spec §4) and the config invariants before the client uses a ruleset. */
export async function verifyRuleset(file: RulesetFile): Promise<RulesetFile> {
  if (globalThis.crypto?.subtle === undefined) {
    throw new Error("Cannot verify the ruleset checksum: Web Crypto needs HTTPS or localhost");
  }
  const actual = await rulesetChecksum(file.config);
  if (actual !== file.checksum) {
    throw new Error(`Ruleset ${file.id} checksum mismatch: expected ${file.checksum}, got ${actual}`);
  }
  assertValidConfig(file.config);
  return file;
}

/** The Street Hoops v1 fixture from `packages/game-rules`, verified. */
export function loadRuleset(): Promise<RulesetFile> {
  return verifyRuleset(fixture);
}

/** Base config with overrides applied; throws a RangeError if the result is not a valid config. */
export function applyOverrides(base: StreetHoopsConfig, overrides: Overrides): StreetHoopsConfig {
  const config = { ...base, ...overrides };
  assertValidConfig(config);
  return config;
}
