import type { KickReason } from "./protocol";

/** Reconnect and kick rules (docs/design/p2-spaces-protocol.md §8). Pure functions; the connection uses them. */

/** Delays (s) of the automatic reconnect attempts; the last one repeats. */
export const RETRY_DELAYS_S: readonly number[] = [0, 2, 5, 10, 20, 30];
/** ±25 % jitter on each delay. */
export const RETRY_JITTER = 0.25;
/** Give up after this long and show "Reconnect". */
export const RETRY_LIMIT_MS = 5 * 60_000;
/** `rate_limited` and `invalid_input` kicks: no reconnect for this long. */
export const ABUSE_COOLDOWN_MS = 60_000;

/**
 * Delay (ms) before reconnect attempt `attempt` (0 = the first), or null to stop: 5 minutes after the
 * connection was lost. `random` is in [0, 1).
 */
export function retryDelay(attempt: number, elapsedMs: number, random: () => number = Math.random): number | null {
  if (elapsedMs >= RETRY_LIMIT_MS) return null;
  const base = RETRY_DELAYS_S[Math.min(attempt, RETRY_DELAYS_S.length - 1)]! * 1000;
  const jitter = 1 + (random() * 2 - 1) * RETRY_JITTER;
  const delay = Math.round(base * jitter);
  // The last attempt still starts inside the 5 minutes.
  return elapsedMs + delay >= RETRY_LIMIT_MS ? Math.max(0, RETRY_LIMIT_MS - elapsedMs - 1) : delay;
}

/** `server_shutdown`: first attempt after a random 2–6 s (spreads the reconnect after a deploy). */
export function shutdownDelay(random: () => number = Math.random): number {
  return Math.round(2000 + random() * 4000);
}

/** `spaces_full` kick (a block move found no room): reconnect by itself after this long. */
export const FULL_RETRY_MS = 60_000;

/**
 * What the client does after a kick:
 * - `auto`: reconnect by itself after `delayMs` (`null`: the 2–6 s `shutdownDelay`), then the normal backoff;
 * - `manual`: a button reconnects (after `cooldownMs`, if any);
 * - `none`: never on this page (another tab owns the connection, suspension);
 * - `leave`: go to the start page (the account is gone or the session ended).
 */
export type KickAction = { kind: "auto"; delayMs: number | null } | { kind: "manual"; cooldownMs: number } | { kind: "none" } | { kind: "leave" };

export interface KickRule {
  action: KickAction;
  message: string;
  /** Label of the button for `manual`. */
  button?: string;
}

export const KICK_RULES: Readonly<Record<KickReason, KickRule>> = {
  server_shutdown: { action: { kind: "auto", delayMs: null }, message: "VYBE is restarting. You will be back in the plaza in a moment." },
  replaced: { action: { kind: "none" }, message: "The plaza is open in another tab or device. Close this one, or play there." },
  idle: { action: { kind: "manual", cooldownMs: 0 }, message: "You left the plaza after 10 minutes without moving.", button: "Rejoin" },
  not_joined: { action: { kind: "manual", cooldownMs: 0 }, message: "The plaza did not open in time.", button: "Try again" },
  rate_limited: {
    action: { kind: "manual", cooldownMs: ABUSE_COOLDOWN_MS },
    message: "You sent too many messages, so the plaza closed. You can come back in a minute.",
    button: "Rejoin",
  },
  invalid_input: {
    action: { kind: "manual", cooldownMs: ABUSE_COOLDOWN_MS },
    message: "The plaza closed after a connection problem. You can come back in a minute.",
    button: "Rejoin",
  },
  suspended: { action: { kind: "none" }, message: "Your account is suspended, so you cannot enter the plaza." },
  account_unavailable: { action: { kind: "leave" }, message: "Your session has ended." },
  spaces_full: { action: { kind: "auto", delayMs: FULL_RETRY_MS }, message: "All plazas are full right now. VYBE tries again in a minute." },
  slow_client: { action: { kind: "auto", delayMs: 0 }, message: "The connection was too slow. Reconnecting…" },
};

/** Text for a non-fatal `error` message (§7). */
export const HUB_ERROR_TEXT = {
  rate_limited: "Slow down: wait a moment before you send more.",
  invalid_input: "That did not work. Try something else.",
  not_joined: "You are not in the plaza yet. Wait a moment.",
} as const;

/** Text for a failed `Join` and for connection problems. */
export const JOIN_ERROR_TEXT = {
  spaces_full: "All plazas are full right now. Try again in a few minutes.",
  rate_limited: "Too many tries to enter the plaza. Wait a minute and try again.",
  already_joined: "You are already in the plaza.",
} as const;
