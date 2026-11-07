import { describe, expect, it } from "vitest";
import { itemNames, outfitItemIds } from "./outfits-panel";

describe("saved outfit helpers", () => {
  it("lists the item ids in slot order without empty places", () => {
    expect(outfitItemIds({ hair: null, top: "t", bottom: "b", shoes: null, emote: "e", accessories: [null, "a"] })).toEqual(["t", "b", "a", "e"]);
  });

  it("names items and marks the ones that are not in the inventory", () => {
    const names = new Map([
      ["t", "Starter Tee"],
      ["b", "Starter Shorts"],
    ]);
    expect(itemNames(["t"], names)).toBe("Starter Tee");
    expect(itemNames(["t", "b"], names)).toBe("Starter Tee and Starter Shorts");
    expect(itemNames(["t", "b", "x"], names)).toBe("Starter Tee, Starter Shorts and 1 item you no longer own");
    expect(itemNames(["x", "y"], names)).toBe("2 items you no longer own");
    expect(itemNames([], names)).toBe("no items");
  });
});
