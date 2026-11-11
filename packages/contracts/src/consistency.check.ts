/**
 * Compile-time guard: the contract and the simulator must describe the same
 * ruleset config and rejection reasons. `pnpm typecheck` fails on drift.
 */
import type { RejectionReason, StreetHoopsConfig, StreetJuggleConfig } from "@vybe/game-rules";
import type { components } from "./generated/vybe-api.ts";

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
type Assert<T extends true> = T;
type Schemas = components["schemas"];

export type ConfigMatchesSimulator = Assert<Equal<Schemas["StreetHoopsConfig"], StreetHoopsConfig>>;
export type JuggleConfigMatchesSimulator = Assert<Equal<Schemas["StreetJuggleConfig"], StreetJuggleConfig>>;

// invalid_event is decided in the API on raw JSON tokens, before the simulator runs (spec §7 step 4).
export type ReasonsMatchSimulator = Assert<Equal<Schemas["RejectionReason"], RejectionReason | "invalid_event">>;
