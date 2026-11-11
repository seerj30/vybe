import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";

/** Contract `HandleInput`: matched case-insensitively by the API. */
export function isHandleInput(value: string): boolean {
  return /^[A-Za-z0-9_]{3,20}$/.test(value);
}

/** Text after a sent request: a reverse request makes the pair friends at once. */
export function requestSentText(result: Schemas["FriendRequestResult"]): string {
  return result.status === "accepted" ? `You and ${result.handle} are now friends.` : `Friend request sent to ${result.handle}.`;
}

/** `recipient_blocked` has a challenge text in messages.ts; a friend request needs its own. */
export function friendErrorMessage(error: unknown): string {
  if (isApiError(error, "recipient_blocked")) return "You blocked this player. Unblock them in Settings to send a friend request.";
  return errorMessage(error);
}

/** ISO week label (`YYYY-Www`) of a UTC date. */
export function isoWeek(date: Date): string {
  const day = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  const weekday = day.getUTCDay() === 0 ? 7 : day.getUTCDay();
  day.setUTCDate(day.getUTCDate() + 4 - weekday); // the Thursday of the week decides the year
  const yearStart = Date.UTC(day.getUTCFullYear(), 0, 1);
  const week = Math.ceil(((day.getTime() - yearStart) / 86_400_000 + 1) / 7);
  return `${day.getUTCFullYear()}-W${String(week).padStart(2, "0")}`;
}

/** The ISO week before the week that starts on `weekStart` (`YYYY-MM-DD`, a Monday). */
export function previousWeek(weekStart: string): string {
  const monday = new Date(`${weekStart}T00:00:00Z`);
  monday.setUTCDate(monday.getUTCDate() - 7);
  return isoWeek(monday);
}

/** "9 Nov – 15 Nov 2026" for a board's week (UTC dates). */
export function weekRange(weekStart: string, weekEnd: string): string {
  const format = (value: string, year: boolean) =>
    new Date(`${value}T00:00:00Z`).toLocaleDateString("en-GB", {
      day: "numeric",
      month: "short",
      ...(year ? { year: "numeric" } : {}),
      timeZone: "UTC",
    });
  return `${format(weekStart, false)} – ${format(weekEnd, true)}`;
}
