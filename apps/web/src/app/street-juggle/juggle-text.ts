import type { Schemas } from "@vybe/contracts";
import type { StreetJuggleConfig } from "@vybe/game-rules";

const REJECTION_TEXT: Record<Schemas["RejectionReason"], string> = {
  not_started: "the run did not start",
  ruleset_mismatch: "the game rules changed",
  invalid_event: "a touch had invalid data",
  out_of_range: "a touch was out of range",
  too_many_events: "there were too many touches",
  non_monotonic: "the touches were out of order",
  cadence_violation: "touches came too fast",
  submitted_too_early: "it claimed more game time than had passed",
};

/** Why a verified Street Juggle run earned no V-Coins (decision 4); the numbers come from the run's ruleset. */
export function juggleNoCoinsText(reason: Schemas["NoCoinsReason"], config: StreetJuggleConfig): string {
  switch (reason) {
    case "too_few_shots":
      return `No coins: too few touches. Touch the ball at least ${config.minEventsForRewards} times to earn V-Coins, XP and quest progress.`;
    case "run_too_short":
      return "No coins: run too short. Play the full run to earn V-Coins, XP and quest progress.";
    case "score_below_minimum":
      return `No coins: score below ${config.minScoreForCoins}. Your XP and quest progress still count.`;
    case "daily_limit":
      return "No coins: you reached today's limit of rewarded runs. The limit resets at 00:00 UTC.";
  }
}

export function juggleRejectionText(reason: Schemas["RejectionReason"]): string {
  return REJECTION_TEXT[reason];
}
