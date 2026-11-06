import { describe, expect, it } from "vitest";
import manifest from "../../../../../assets/manifests/avatar.v1.json";
import { DEFAULT_PRESETS, describeAvatar, FALLBACK_COLOR, hairColor, PRESET_GROUPS, presetLabel, samePresets, skinColor } from "./presets";

describe("avatar presets", () => {
  it("offers every manifest id, so the client and the API agree", () => {
    const ids = Object.fromEntries(PRESET_GROUPS.map((g) => [g.key, g.options.map((o) => o.id)]));
    expect(ids).toEqual({
      bodyPresetId: manifest.bodyPresets,
      skinToneId: manifest.skinTones,
      facePresetId: manifest.faces,
      hairPresetId: manifest.hairs,
      hairColorId: manifest.hairColors,
    });
  });

  it("has a color for every manifest skin tone and hair color", () => {
    for (const id of manifest.skinTones) expect(skinColor(id)).not.toBe(FALLBACK_COLOR);
    for (const id of manifest.hairColors) expect(hairColor(id)).not.toBe(FALLBACK_COLOR);
  });

  it("uses the fallback color for an unknown id", () => {
    expect(skinColor("skin_99")).toBe(FALLBACK_COLOR);
  });

  it("labels and describes presets in words", () => {
    expect(presetLabel("bodyPresetId", "body_b")).toBe("Body B");
    expect(presetLabel("skinToneId", "skin_03")).toBe("Tone 3");
    expect(describeAvatar(DEFAULT_PRESETS)).toBe("Body A, tone 3, face 1, brown short hair");
  });

  it("compares presets by value", () => {
    expect(samePresets(DEFAULT_PRESETS, { ...DEFAULT_PRESETS })).toBe(true);
    expect(samePresets(DEFAULT_PRESETS, { ...DEFAULT_PRESETS, hairColorId: "red" })).toBe(false);
  });
});
