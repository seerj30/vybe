import type { Schemas } from "@vybe/contracts";
import manifest from "../../../../../assets/manifests/avatar.v1.json";

export type AvatarPresets = Schemas["AvatarPresets"];
export type PresetKey = keyof AvatarPresets;

export interface PresetGroup {
  key: PresetKey;
  legend: string;
  options: readonly PresetOption[];
}

export interface PresetOption {
  id: string;
  label: string;
}

/** Placeholder 2D colors until the art pipeline ships (spec §17 item 3). Unknown ids use the fallback. */
const SKIN_COLORS: Record<string, string> = {
  skin_01: "#f6d7c3",
  skin_02: "#eac0a0",
  skin_03: "#c99a72",
  skin_04: "#a87650",
  skin_05: "#7c5236",
  skin_06: "#53351f",
};
const HAIR_COLORS: Record<string, string> = {
  black: "#1c1c22",
  brown: "#6b4226",
  blond: "#e0bd6e",
  red: "#a8432a",
  blue: "#3f6ee8",
};
export const FALLBACK_COLOR = "#8a90a6";

const HAIR_LABELS: Record<string, string> = {
  hair_short: "Short",
  hair_long: "Long",
  hair_curly: "Curly",
  hair_buzz: "Buzz",
};

function numbered(prefix: string, id: string): string {
  const n = Number(id.split("_")[1]);
  return Number.isInteger(n) ? `${prefix} ${n}` : id;
}

function capitalized(id: string): string {
  return id.charAt(0).toUpperCase() + id.slice(1);
}

export function presetLabel(key: PresetKey, id: string): string {
  switch (key) {
    case "bodyPresetId":
      return `Body ${(id.split("_")[1] ?? id).toUpperCase()}`;
    case "skinToneId":
      return numbered("Tone", id);
    case "facePresetId":
      return numbered("Face", id);
    case "hairPresetId":
      return HAIR_LABELS[id] ?? id;
    case "hairColorId":
      return capitalized(id);
  }
}

function group(key: PresetKey, legend: string, ids: readonly string[]): PresetGroup {
  return { key, legend, options: ids.map((id) => ({ id, label: presetLabel(key, id) })) };
}

/** Preset choices from `assets/manifests/avatar.v1.json`, the same file the API validates against. */
export const PRESET_GROUPS: readonly PresetGroup[] = [
  group("bodyPresetId", "Body", manifest.bodyPresets),
  group("skinToneId", "Skin tone", manifest.skinTones),
  group("facePresetId", "Face", manifest.faces),
  group("hairPresetId", "Hair style", manifest.hairs),
  group("hairColorId", "Hair color", manifest.hairColors),
];

export const DEFAULT_PRESETS: AvatarPresets = manifest.defaults;

export function skinColor(id: string): string {
  return SKIN_COLORS[id] ?? FALLBACK_COLOR;
}

export function hairColor(id: string): string {
  return HAIR_COLORS[id] ?? FALLBACK_COLOR;
}

export function pickPresets(source: AvatarPresets): AvatarPresets {
  const { bodyPresetId, skinToneId, facePresetId, hairPresetId, hairColorId } = source;
  return { bodyPresetId, skinToneId, facePresetId, hairPresetId, hairColorId };
}

export function samePresets(a: AvatarPresets, b: AvatarPresets): boolean {
  return PRESET_GROUPS.every(({ key }) => a[key] === b[key]);
}

/** Text alternative for the portrait, so the avatar never depends on color alone. */
export function describeAvatar(p: AvatarPresets): string {
  return [
    presetLabel("bodyPresetId", p.bodyPresetId),
    presetLabel("skinToneId", p.skinToneId).toLowerCase(),
    presetLabel("facePresetId", p.facePresetId).toLowerCase(),
    `${presetLabel("hairColorId", p.hairColorId).toLowerCase()} ${presetLabel("hairPresetId", p.hairPresetId).toLowerCase()} hair`,
  ].join(", ");
}
