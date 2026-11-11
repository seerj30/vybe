import { describe, expect, it } from "vitest";
import { type Achievement, categoryLabel, groupByCategory, matches, progressText, stateOf } from "./achievement-view";

const base: Achievement = {
  code: "runs_10",
  name: "Warming Up",
  description: "Finish 10 runs.",
  category: "play",
  tier: "bronze",
  rewardXp: 25,
  target: 10,
  progress: 0,
  unlockedAt: null,
};

describe("achievement view helpers", () => {
  it("tells unlocked, in progress and locked apart", () => {
    expect(stateOf({ ...base, progress: 10, unlockedAt: "2026-11-11T10:00:00Z" })).toBe("unlocked");
    expect(stateOf({ ...base, progress: 3 })).toBe("progress");
    expect(stateOf(base)).toBe("locked");
  });

  it("filters and groups in the API order", () => {
    const list: Achievement[] = [
      { ...base, code: "a", category: "games", progress: 1, target: 1, unlockedAt: "2026-11-11T10:00:00Z" },
      { ...base, code: "b", category: "play", progress: 2 },
      { ...base, code: "c", category: "games" },
    ];
    expect(groupByCategory(list, "all").map((g) => [g.category, g.items.map((i) => i.code)])).toEqual([
      ["games", ["a", "c"]],
      ["play", ["b"]],
    ]);
    expect(groupByCategory(list, "locked").map((g) => g.items.map((i) => i.code))).toEqual([["c"]]);
    expect(matches(list[1]!, "progress")).toBe(true);
  });

  it("formats progress and labels", () => {
    expect(progressText({ progress: 1500, target: 7500 })).toBe("1,500 / 7,500");
    expect(categoryLabel("competition")).toBe("Challenges and leaderboard");
    expect(categoryLabel("new_kind")).toBe("new_kind");
  });
});
