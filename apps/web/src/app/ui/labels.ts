import type { Schemas } from "@vybe/contracts";

export const SLOT_LABELS: Record<Schemas["ItemSlot"], string> = {
  hair: "Hair",
  top: "Top",
  bottom: "Bottom",
  shoes: "Shoes",
  accessory: "Accessory",
  emote: "Emote",
};

export const SLOTS = Object.keys(SLOT_LABELS) as Schemas["ItemSlot"][];

export const LEDGER_LABELS: Record<Schemas["LedgerReason"], string> = {
  game_reward: "Game reward",
  quest_reward: "Quest reward",
  purchase: "Purchase",
  level_reward: "Level-up reward",
  challenge_reward: "Challenge reward",
  admin_adjustment: "Adjustment by VYBE",
};
