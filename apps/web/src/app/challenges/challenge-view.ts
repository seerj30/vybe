import type { Schemas } from "@vybe/contracts";

export type Challenge = Schemas["Challenge"];
export type ChallengeStatus = Schemas["ChallengeStatus"];

/** The caller's side of a challenge. */
export type Side = "creator" | "recipient";

export type Outcome = "won" | "lost" | "tie";

export const STATUS_LABELS: Record<ChallengeStatus, string> = {
  open: "Waiting for a player",
  accepted: "Accepted",
  completed: "Finished",
  expired: "Expired",
};

/**
 * The invite token of a share link (`{ShareBaseUrl}/challenge/{token}`), so the app can open the link
 * on its own origin. Null when the URL has another form.
 */
export function inviteToken(shareUrl: string): string | null {
  let path: string;
  try {
    path = new URL(shareUrl).pathname;
  } catch {
    return null;
  }
  const match = /^\/challenge\/([A-Za-z0-9_-]+)$/.exec(path);
  return match?.[1] ?? null;
}

export function sideOf(challenge: Challenge, myHandle: string): Side {
  return challenge.creator.handle === myHandle ? "creator" : "recipient";
}

/** The other participant, or null while nobody accepted the open invite. */
export function opponentOf(challenge: Challenge, side: Side): Schemas["PlayerSummary"] | null {
  return side === "creator" ? challenge.recipient : challenge.creator;
}

/** The result for the caller. A tie keeps the title with the creator, but nobody wins (spec §2 J2). */
export function outcomeFor(challenge: Challenge, side: Side): Outcome | null {
  switch (challenge.result) {
    case null:
      return null;
    case "tie":
      return "tie";
    case "creator_win":
      return side === "creator" ? "won" : "lost";
    case "recipient_win":
      return side === "recipient" ? "won" : "lost";
  }
}

export const OUTCOME_LABELS: Record<Outcome, string> = {
  won: "You won",
  lost: "You lost",
  tie: "Tie",
};

/** Text for a timestamp in the player's locale, for example "6 Nov, 14:05". */
export function shortTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}
