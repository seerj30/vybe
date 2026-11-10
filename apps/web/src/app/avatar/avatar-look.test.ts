import { describe, expect, it } from "vitest";
import { AVATAR_3D, itemAsset, itemPicture } from "./asset-manifest";
import { EMPTY_OUTFIT, fallbackOutfit, type Outfit, outfitFromEquipped, partUrls, resolveLook, withItem } from "./avatar-look";
import { DEFAULT_PRESETS, PRESET_GROUPS, skinColor, hairColor } from "./presets";

const STARTER: Outfit = { ...EMPTY_OUTFIT, top: "top_starter_tee", bottom: "bottom_starter_shorts", shoes: "shoes_starter_kicks" };
const BODY_B = { ...DEFAULT_PRESETS, bodyPresetId: "body_b" };

describe("art manifests", () => {
  it("has 3D data for every avatar preset id, with the 2D portrait colors", () => {
    const ids = Object.fromEntries(PRESET_GROUPS.map((g) => [g.key, g.options.map((o) => o.id)]));
    expect(Object.keys(AVATAR_3D.bodyPresets).sort()).toEqual([...(ids["bodyPresetId"] ?? [])].sort());
    expect(AVATAR_3D.faces).toEqual(ids["facePresetId"]);
    expect([...AVATAR_3D.hairs].sort()).toEqual([...(ids["hairPresetId"] ?? [])].sort());
    for (const [id, color] of Object.entries(AVATAR_3D.skinTones)) expect(color).toBe(skinColor(id));
    for (const [id, color] of Object.entries(AVATAR_3D.hairColors)) expect(color).toBe(hairColor(id));
  });

  it("serves every asset from an immutable same-origin path", () => {
    const urls = [AVATAR_3D.base.assetUrl, ...Object.values(AVATAR_3D.starterItems).flatMap((sku) => [itemAsset(sku)!.assetUrl, itemAsset(sku)!.thumbnailUrl])];
    for (const url of urls) expect(url).toMatch(/^\/assets\/3d\/(avatar|items|thumbs)\/[a-z0-9_]+\.[0-9a-f]{12}\.(glb|png)$/);
  });

  it("uses the pipeline thumbnail and keeps the API picture for unknown skus", () => {
    expect(itemPicture("top_court_jersey", "https://cdn.example/x.png")).toBe(itemAsset("top_court_jersey")!.thumbnailUrl);
    expect(itemPicture("top_from_the_future", "https://cdn.example/x.png")).toBe("https://cdn.example/x.png");
  });
});

describe("resolveLook", () => {
  it("dresses the avatar and hides the covered body regions", () => {
    const look = resolveLook(DEFAULT_PRESETS, STARTER);
    expect(look.parts.map((p) => p.sku)).toEqual(["top_starter_tee", "bottom_starter_shorts", "shoes_starter_kicks"]);
    expect(look.hiddenRegions).toEqual(["feet", "hips", "torso", "upper_arms", "upper_legs"]);
    expect(look.presetHair).toBe(DEFAULT_PRESETS.hairPresetId);
    expect(look.face).toBe(DEFAULT_PRESETS.facePresetId);
    expect(look.morph).toBe(0);
    expect(look.skinColor).toBe(skinColor(DEFAULT_PRESETS.skinToneId));
    expect(look.skipped).toEqual([]);
  });

  it("uses the body_b shape key for body B", () => {
    expect(resolveLook(BODY_B, STARTER).morph).toBe(1);
  });

  it("applies the color variant of a sku that shares a mesh", () => {
    const away = resolveLook(DEFAULT_PRESETS, { ...STARTER, top: "top_away_jersey" }).parts[0]!;
    const home = resolveLook(DEFAULT_PRESETS, { ...STARTER, top: "top_court_jersey" }).parts[0]!;
    expect(away.url).toBe(home.url);
    expect(away.colors).toEqual({ primary: "#e63946", secondary: "#1d3557", trim: "#f1faee" });
    expect(home.colors).toEqual({});
  });

  it("replaces a top that does not fit the body with the starter top", () => {
    const look = resolveLook(BODY_B, { ...STARTER, top: "top_neon_hoodie" });
    expect(look.parts[0]!.sku).toBe("top_starter_tee");
    expect(look.skipped).toEqual(["top_neon_hoodie"]);
  });

  it("leaves off unknown skus without failing", () => {
    const look = resolveLook(DEFAULT_PRESETS, { ...EMPTY_OUTFIT, hair: "hair_unknown", accessories: ["acc_unknown", null] });
    expect(look.parts).toEqual([]);
    expect(look.skipped).toEqual(["acc_unknown", "hair_unknown"]);
    expect(look.presetHair).toBe(DEFAULT_PRESETS.hairPresetId);
  });

  it("puts a hair item on instead of the preset hair, and a hat hides both", () => {
    const hair = resolveLook(DEFAULT_PRESETS, { ...STARTER, hair: "hair_mohawk" });
    expect(hair.presetHair).toBeNull();
    expect(hair.parts.map((p) => p.key)).toContain("hair");
    const hat = resolveLook(DEFAULT_PRESETS, { ...STARTER, hair: "hair_mohawk", accessories: ["acc_beanie", null] });
    expect(hat.presetHair).toBeNull();
    expect(hat.parts.map((p) => p.key)).not.toContain("hair");
    expect(hat.hiddenRegions).toContain("hair");
  });

  it("mirrors a left-arm accessory at attachment point 2 and keeps head items on point 1", () => {
    const look = resolveLook(DEFAULT_PRESETS, { ...STARTER, accessories: ["acc_shades", "acc_sweatband"] });
    const nodes = look.parts.filter((p) => p.key.startsWith("accessory")).map((p) => p.node);
    expect(nodes).toEqual(["shades_point_1", "sweatband_point_2"]);
  });

  it("lists each file once", () => {
    const look = resolveLook(DEFAULT_PRESETS, { ...STARTER, accessories: ["acc_sweatband", "acc_chain"] });
    expect(partUrls(look)).toHaveLength(5);
  });
});

describe("outfits", () => {
  it("maps equipped item ids to skus through the inventory", () => {
    const outfit = outfitFromEquipped(
      { hair: null, top: "t1", bottom: "b1", shoes: "missing", emote: null, accessories: [null, "a1"] },
      [
        { itemId: "t1", sku: "top_court_jersey" },
        { itemId: "b1", sku: "bottom_joggers" },
        { itemId: "a1", sku: "acc_chain" },
      ],
    );
    expect(outfit).toEqual({ hair: null, top: "top_court_jersey", bottom: "bottom_joggers", shoes: null, accessories: [null, "acc_chain"] });
  });

  it("tries an item on without changing the original outfit", () => {
    const tried = withItem(STARTER, "top", "top_varsity_jacket");
    expect(tried.top).toBe("top_varsity_jacket");
    expect(STARTER.top).toBe("top_starter_tee");
    expect(withItem({ ...STARTER, accessories: ["acc_chain", null] }, "accessory", "acc_shades").accessories).toEqual(["acc_chain", "acc_shades"]);
    expect(withItem(STARTER, "emote", "emote_wave")).toBe(STARTER);
  });

  it("falls back to the starter garment for a failed item and drops failed extras (E10)", () => {
    const outfit: Outfit = { hair: "hair_braids", top: "top_varsity_jacket", bottom: "bottom_joggers", shoes: "shoes_runners", accessories: ["acc_chain", null] };
    const failed = new Set(["top_varsity_jacket", "shoes_runners", "shoes_starter_kicks", "hair_braids", "acc_chain"]);
    expect(fallbackOutfit(outfit, failed)).toEqual({ hair: null, top: "top_starter_tee", bottom: "bottom_joggers", shoes: null, accessories: [null, null] });
    expect(outfit.top).toBe("top_varsity_jacket");
  });
});
