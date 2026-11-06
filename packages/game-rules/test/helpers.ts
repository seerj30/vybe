import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { EvaluateInput, ShotEvent, StreetHoopsConfig } from "../src/types.ts";

export const FIXTURES_DIR = fileURLToPath(new URL("../fixtures/", import.meta.url));

export function readJson<T>(relativePath: string): T {
  return JSON.parse(readFileSync(`${FIXTURES_DIR}${relativePath}`, "utf8")) as T;
}

export interface RulesetFile {
  id: string;
  gameId: string;
  version: number;
  config: StreetHoopsConfig;
  checksum: string;
}

export const RULESET = readJson<RulesetFile>("rulesets/street-hoops-v1.json");
export const CONFIG = RULESET.config;

export const STARTED_AT_MS = 1_000;
export const SUBMIT_DEADLINE_MS = STARTED_AT_MS + CONFIG.durationMs + CONFIG.graceMs;

/** A valid, active session submitted right after the game time ends. */
export function makeInput(
  events: ShotEvent[],
  overrides: Partial<Pick<EvaluateInput, "seed">> & {
    status?: EvaluateInput["session"]["status"];
    submittedAtMs?: number;
    submittedRulesetId?: string;
  } = {},
): EvaluateInput {
  return {
    config: CONFIG,
    rulesetId: RULESET.id,
    seed: overrides.seed ?? 12345,
    session: {
      status: overrides.status ?? "active",
      startedAtMs: STARTED_AT_MS,
      submitDeadlineMs: SUBMIT_DEADLINE_MS,
    },
    submission: {
      rulesetId: overrides.submittedRulesetId ?? RULESET.id,
      submittedAtMs: overrides.submittedAtMs ?? STARTED_AT_MS + CONFIG.durationMs + 500,
      events,
    },
  };
}
