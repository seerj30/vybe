import { assertValidJuggleConfig, rulesetChecksum, type StreetJuggleConfig } from "@vybe/game-rules";
import fixture from "../../../../../packages/game-rules/fixtures/rulesets/street-juggle-v1.json";
import type { RulesetFile } from "../street-hoops/ruleset";

export type JuggleRuleset = RulesetFile<StreetJuggleConfig>;

/** Config keys the dev tuning panel can override (practice only). */
export const JUGGLE_TUNABLE_KEYS = [
  "goodDy",
  "perfectDy",
  "gravityStart",
  "gravityStep",
  "launchMin",
  "launchMax",
  "whiffLockMs",
  "respawnMs",
] as const satisfies readonly (keyof StreetJuggleConfig)[];

export type JuggleTunableKey = (typeof JUGGLE_TUNABLE_KEYS)[number];
export type JuggleOverrides = Partial<Record<JuggleTunableKey, number>>;

/** Checks the checksum (spec §4) and the config invariants before the client uses a ruleset. */
export async function verifyJuggleRuleset(file: JuggleRuleset): Promise<JuggleRuleset> {
  if (globalThis.crypto?.subtle === undefined) {
    throw new Error("Cannot verify the ruleset checksum: Web Crypto needs HTTPS or localhost");
  }
  const actual = await rulesetChecksum(file.config);
  if (actual !== file.checksum) {
    throw new Error(`Ruleset ${file.id} checksum mismatch: expected ${file.checksum}, got ${actual}`);
  }
  assertValidJuggleConfig(file.config);
  return file;
}

/** The Street Juggle v1 fixture from `packages/game-rules`, verified (practice mode). */
export function loadJuggleRuleset(): Promise<JuggleRuleset> {
  return verifyJuggleRuleset(fixture);
}

/** Base config with overrides applied; throws a RangeError if the result is not a valid config. */
export function applyJuggleOverrides(base: StreetJuggleConfig, overrides: JuggleOverrides): StreetJuggleConfig {
  const config = { ...base, ...overrides };
  assertValidJuggleConfig(config);
  return config;
}
