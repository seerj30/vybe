import { newIdempotencyKey } from "../api/api-client";

const PREFIX = "vybe.challengeKey.";

/** One create (or rematch) request: the key belongs to this exact body, never to another one. */
export interface CreateRequest {
  sessionId: string;
  rematchOf?: string;
  to?: string;
}

type KeyStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;

/** The storage name of a request's key. Handles match case-insensitively on the server. */
export function keyName(request: CreateRequest): string {
  return PREFIX + JSON.stringify([request.sessionId, request.rematchOf ?? null, request.to?.toLowerCase() ?? null]);
}

/**
 * One Idempotency-Key per create request, kept for the browser tab. A reload or a retry of the same
 * request sends the same key, so the API answers with the same share link (ADR 0004 #2). The key stays
 * after a success on purpose: the replay is the only way to show the creator the link again, and it
 * sends the same body, so it cannot mismatch.
 */
export function challengeKey(request: CreateRequest, storage: KeyStorage | undefined = globalThis.sessionStorage): string {
  try {
    if (storage === undefined) return newIdempotencyKey();
    const stored = storage.getItem(keyName(request));
    if (stored !== null) return stored;
    const key = newIdempotencyKey();
    storage.setItem(keyName(request), key);
    return key;
  } catch {
    // Storage blocked: the key lives for this page only; a reload answers session_already_used.
    return newIdempotencyKey();
  }
}

/** Forgets the key after a known failure: no challenge exists, so a fresh key is safe. */
export function forgetChallengeKey(request: CreateRequest, storage: KeyStorage | undefined = globalThis.sessionStorage): void {
  try {
    storage?.removeItem(keyName(request));
  } catch {
    // Storage blocked: nothing was stored.
  }
}
