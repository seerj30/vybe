import { ApiError, type ErrorCode } from "./api-client";

/** Player-facing text for API failures. Codes without an entry get the generic text. */
const MESSAGES: Partial<Record<ErrorCode, string>> = {
  network_error: "No connection to VYBE. Check your connection and try again.",
  internal_error: "VYBE has a problem right now. Try again in a moment.",
  rate_limited: "Too many attempts. Wait a minute and try again.",
  request_in_progress: "VYBE is still working on your last request. Try again in a moment.",
  unauthenticated: "Your guest session has ended.",
  account_suspended: "Your account is suspended.",
  revision_conflict: "Your avatar changed in another tab or device. Check your choices and save again.",
  invalid_preset: "One of the choices is not available. Pick another one.",
  incompatible_equipment: "Some equipped items do not fit this body. Unequip them in the wardrobe first.",
};

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return MESSAGES[error.code] ?? "Something went wrong. Try again.";
  return "Something went wrong. Try again.";
}
