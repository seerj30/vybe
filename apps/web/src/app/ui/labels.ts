import type { Schemas } from "@vybe/contracts";

export const SLOT_LABELS: Record<Schemas["ItemSlot"], string> = {
  hair: "Hair",
  top: "Top",
  bottom: "Bottom",
  shoes: "Shoes",
  accessory: "Accessory",
  emote: "Emote",
  furniture: "Furniture",
  wall_decor: "Wall decor",
  room_wall: "Walls",
  room_floor: "Floor",
};

export const SLOTS = Object.keys(SLOT_LABELS) as Schemas["ItemSlot"][];

/** Room items (docs/design/p1-my-room.md) are placed in the room, never worn. */
export const ROOM_SLOTS: readonly Schemas["ItemSlot"][] = ["furniture", "wall_decor", "room_wall", "room_floor"];

export const AVATAR_SLOTS = SLOTS.filter((slot) => !ROOM_SLOTS.includes(slot));

export function isRoomSlot(slot: Schemas["ItemSlot"]): boolean {
  return ROOM_SLOTS.includes(slot);
}

export const LEDGER_LABELS: Record<Schemas["LedgerReason"], string> = {
  game_reward: "Game reward",
  quest_reward: "Quest reward",
  purchase: "Purchase",
  level_reward: "Level-up reward",
  challenge_reward: "Challenge reward",
  admin_adjustment: "Adjustment by VYBE",
};
