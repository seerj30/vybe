import type { Schemas } from "@vybe/contracts";

export type Achievement = Schemas["Achievement"];
export type AchievementTier = Schemas["AchievementTier"];
export type AchievementFilter = "all" | "unlocked" | "progress" | "locked";

/** P1 achievements (docs/design/p1-achievements.md): display helpers for the list, the badges and the run toast. */
export const TIER_LABELS: Record<AchievementTier, string> = { bronze: "Bronze", silver: "Silver", gold: "Gold" };

/** Category headings; an unknown category (new content) shows its code. */
const CATEGORY_LABELS: Record<string, string> = {
  games: "Games",
  play: "Play",
  competition: "Challenges and leaderboard",
  social: "Friends",
  collection: "Collection",
};

export const FILTERS: readonly { value: AchievementFilter; label: string }[] = [
  { value: "all", label: "All" },
  { value: "unlocked", label: "Unlocked" },
  { value: "progress", label: "In progress" },
  { value: "locked", label: "Locked" },
];

export function categoryLabel(category: string): string {
  return CATEGORY_LABELS[category] ?? category;
}

export type AchievementState = "unlocked" | "progress" | "locked";

/** Unlocked; in progress (some progress, not done); locked (no progress yet). */
export function stateOf(a: Pick<Achievement, "unlockedAt" | "progress">): AchievementState {
  if (a.unlockedAt !== null) return "unlocked";
  return a.progress > 0 ? "progress" : "locked";
}

export function matches(a: Achievement, filter: AchievementFilter): boolean {
  return filter === "all" || stateOf(a) === filter;
}

/** Groups in the API order (the manifest order), keeping only the achievements that match the filter. */
export function groupByCategory(list: readonly Achievement[], filter: AchievementFilter): { category: string; items: Achievement[] }[] {
  const groups: { category: string; items: Achievement[] }[] = [];
  for (const a of list) {
    if (!matches(a, filter)) continue;
    let group = groups.find((g) => g.category === a.category);
    if (group === undefined) {
      group = { category: a.category, items: [] };
      groups.push(group);
    }
    group.items.push(a);
  }
  return groups;
}

/** "3 / 10" with thousands separators, for score targets such as 7,500. */
export function progressText(a: Pick<Achievement, "progress" | "target">): string {
  return `${a.progress.toLocaleString("en-US")} / ${a.target.toLocaleString("en-US")}`;
}

export function unlockedText(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}
