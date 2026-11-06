import { newIdempotencyKey } from "../api/api-client";

const PREFIX = "vybe.challengeKey.";

/**
 * One Idempotency-Key per creator run, kept for the browser tab. A reload or a retry of the same
 * create sends the same key, so the API answers with the same share link (ADR 0004 #2).
 */
export function challengeKey(sessionId: string): string {
  try {
    const stored = sessionStorage.getItem(PREFIX + sessionId);
    if (stored !== null) return stored;
    const key = newIdempotencyKey();
    sessionStorage.setItem(PREFIX + sessionId, key);
    return key;
  } catch {
    // Storage blocked: the key lives for this call only; a repeat answers session_already_used.
    return newIdempotencyKey();
  }
}

/** Forgets the key after a known failure: no challenge exists, so a fresh key is safe (and the body may change). */
export function forgetChallengeKey(sessionId: string): void {
  try {
    sessionStorage.removeItem(PREFIX + sessionId);
  } catch {
    // Storage blocked: nothing was stored.
  }
}
