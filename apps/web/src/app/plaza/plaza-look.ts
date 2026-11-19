import { type Look as AvatarLook, type Outfit, fallbackOutfit, resolveLook } from "../avatar/avatar-look";
import type { AvatarPresets } from "../avatar/presets";
import type { Look } from "./protocol";

/** Presets of a hub look. */
export function presetsOf(look: Look): AvatarPresets {
  const { bodyPresetId, skinToneId, facePresetId, hairPresetId, hairColorId } = look;
  return { bodyPresetId, skinToneId, facePresetId, hairPresetId, hairColorId };
}

/** Outfit (SKUs) of a hub look. The hub already sends SKUs, so no inventory lookup is needed. */
export function outfitOf(look: Look): Outfit {
  const { hair, top, bottom, shoes, accessories } = look.equipped;
  return { hair, top, bottom, shoes, accessories: [accessories[0] ?? null, accessories[1] ?? null] };
}

/**
 * The renderer look of a member: the same rules as the avatar viewer (body compatibility, coverage masks,
 * starter items for garments that failed to load). An unknown SKU (newer server catalog) is left off.
 */
export function avatarLook(look: Look, failed: ReadonlySet<string> = new Set()): AvatarLook {
  return resolveLook(presetsOf(look), fallbackOutfit(outfitOf(look), failed));
}
