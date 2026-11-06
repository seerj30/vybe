import type { Schemas } from "@vybe/contracts";
import type { ShotEvent } from "@vybe/game-rules";
import { type ApiClient, newIdempotencyKey } from "../api/api-client";
import { type RulesetFile, verifyRuleset } from "./ruleset";

/** A started API session: the client clock starts at 0 when `startSession` answers. */
export interface OnlineRun {
  sessionId: string;
  ruleset: RulesetFile;
  seed: number;
  /** One key for this run's submission, reused on every retry (spec §2 J1). */
  submitKey: string;
}

export type RunOutcome =
  | { status: "verified"; score: number; rewards: Schemas["Rewards"] }
  | { status: "rejected"; reason: Schemas["RejectionReason"] }
  | { status: "expired" };

const POLL_ATTEMPTS = 10;
const POLL_INTERVAL_MS = 1000;

/**
 * Street Hoops on API sessions (spec §5 gameplay, §7): create → verify ruleset → start → submit.
 * The local simulator gives instant feedback; only the server outcome counts.
 */
export class OnlineSessions {
  private readonly rulesets = new Map<string, RulesetFile>();

  constructor(
    private readonly api: ApiClient,
    private readonly verify: (file: RulesetFile) => Promise<RulesetFile> = verifyRuleset,
    private readonly sleep: (ms: number) => Promise<void> = (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
  ) {}

  /** With `challengeId`, the recipient run of an accepted challenge (the creator run's seed and ruleset). */
  async prepare(challengeId?: string): Promise<OnlineRun> {
    const created = await this.api.createStreetHoopsSession(challengeId === undefined ? {} : { challengeId });
    const ruleset = await this.ruleset(created.rulesetId, created.rulesetChecksum);
    const started = await this.api.startSession(created.sessionId);
    return { sessionId: created.sessionId, ruleset, seed: started.seed, submitKey: newIdempotencyKey() };
  }

  /** The final outcome, or null while the server is still verifying (call `reconcile` later). */
  async submit(run: OnlineRun, events: readonly ShotEvent[], clientScore: number): Promise<RunOutcome | null> {
    const body = { rulesetId: run.ruleset.id, events: [...events], clientReportedScore: clientScore };
    const response = await this.api.submitSession(run.sessionId, body, run.submitKey);
    if (response.status !== "submitted") return outcomeOf(response);
    for (let attempt = 0; attempt < POLL_ATTEMPTS; attempt++) {
      await this.sleep(POLL_INTERVAL_MS);
      const outcome = await this.reconcile(run.sessionId);
      if (outcome !== null) return outcome;
    }
    return null;
  }

  /** Reads the session: the outcome if it is final, else null (spec §2 J1: poll before a retry). */
  async reconcile(sessionId: string): Promise<RunOutcome | null> {
    return sessionOutcome(await this.api.getSession(sessionId));
  }

  private async ruleset(id: string, checksum: string): Promise<RulesetFile> {
    const cached = this.rulesets.get(id);
    if (cached !== undefined && cached.checksum === checksum) return cached;
    const ruleset = await this.api.getRuleset(id);
    if (ruleset.checksum !== checksum) {
      throw new Error(`Ruleset ${id} checksum does not match the session`);
    }
    const verified = await this.verify({ id: ruleset.id, config: ruleset.config, checksum: ruleset.checksum });
    this.rulesets.set(id, verified);
    return verified;
  }
}

function outcomeOf(result: Schemas["SubmitResult"]): RunOutcome {
  switch (result.status) {
    case "verified":
      return { status: "verified", score: result.score, rewards: result.rewards };
    case "rejected":
      return { status: "rejected", reason: result.rejectionReason };
    case "expired":
      return { status: "expired" };
  }
}

export function sessionOutcome(session: Schemas["GameSession"]): RunOutcome | null {
  switch (session.status) {
    case "verified":
      return session.score !== null && session.rewards !== null
        ? { status: "verified", score: session.score, rewards: session.rewards }
        : null;
    case "rejected":
      return session.rejectionReason !== null ? { status: "rejected", reason: session.rejectionReason } : null;
    case "expired":
      return { status: "expired" };
    default:
      return null;
  }
}

const REJECTION_TEXT: Record<Schemas["RejectionReason"], string> = {
  not_started: "the run did not start",
  ruleset_mismatch: "the game rules changed",
  invalid_event: "a shot had invalid data",
  out_of_range: "a shot was out of range",
  too_many_events: "there were too many shots",
  non_monotonic: "the shots were out of order",
  cadence_violation: "shots came too fast",
  submitted_too_early: "it claimed more game time than had passed",
};

export function rejectionText(reason: Schemas["RejectionReason"]): string {
  return REJECTION_TEXT[reason];
}
