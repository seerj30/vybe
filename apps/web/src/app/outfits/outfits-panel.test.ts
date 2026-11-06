import { describe, expect, it } from "vitest";
import { itemNames, outfitItemIds } from "./outfits-panel";

describe("saved outfit helpers", () => {
  it("lists the item ids in slot order without empty places", () => {
    expect(outfitItemIds({ hair: null, top: "t", bottom: "b", shoes: null, emote: "e", accessories: [null, "a"] })).toEqual(["t", "b", "a", "e"]);
  });

  it("names items and marks the ones that are not in the inventory", () => {
    const names = new Map([["t", "Starter Tee"]]);
    expect(itemNames(["t", "x"], names)).toBe("Starter Tee, an item you do not own");
    expect(itemNames([], names)).toBe("no items");
  });
});
