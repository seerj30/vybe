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
  insufficient_funds: "You do not have enough V-Coins for this item.",
  already_owned: "You already own this item.",
  level_too_low: "Your level is too low for this item.",
  item_unavailable: "This item is not available any more.",
  item_not_purchasable: "This item cannot be bought.",
  item_not_owned: "You do not own this item.",
  incompatible_item: "This item does not fit your avatar's body.",
  item_inactive: "This item cannot be worn any more.",
  item_already_equipped: "You already wear this item on the other attachment point.",
  incompatible_equipment: "Some equipped items do not fit this body. Unequip them in the wardrobe first.",
  // Challenges, quests, safety and saved outfits (stream G).
  challenge_unavailable: "This challenge is not available.",
  challenge_expired: "This challenge has expired.",
  challenge_already_accepted: "Another player already accepted this challenge.",
  challenge_session_exists: "You already played your run for this challenge.",
  self_accept: "You cannot accept your own challenge.",
  self_challenge: "You cannot challenge yourself.",
  session_not_eligible: "This run cannot start a challenge. Use a verified run with enough shots.",
  session_already_used: "You already made a challenge from this run. Play a new run to send another one.",
  recipient_blocked: "You blocked this player. Unblock them to send a challenge.",
  challenge_not_completed: "A rematch is possible only after the challenge is finished.",
  rematch_already_created: "You already sent a rematch for this challenge.",
  quest_not_completed: "Finish this quest before you claim it.",
  quest_already_claimed: "You already claimed this quest.",
  quest_claim_expired: "The time to claim this quest has ended.",
  self_block: "You cannot block yourself.",
  self_report: "You cannot report yourself.",
  outfit_limit_reached: "You can save 3 outfits. Delete one to save a new one.",
  outfit_items_invalid: "Some items in this outfit are not yours any more or do not fit. Nothing changed.",
};

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return MESSAGES[error.code] ?? "Something went wrong. Try again.";
  return "Something went wrong. Try again.";
}
