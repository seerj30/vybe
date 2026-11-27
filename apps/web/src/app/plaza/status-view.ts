import type { PlazaStatus } from "./plaza-client";

/** The status overlay of the spaces page (plaza and rooms): pure functions, so they are tested without Angular. */

/** Plaza texts for a room: the same rules, another place. */
export function inRoom(text: string): string {
  return text.replace(/plazas/g, "rooms").replace(/plaza/g, "room");
}

export interface StatusView {
  title: string;
  text: string;
  busy: boolean;
  action: "retry" | "reload" | "start" | null;
  button: string;
  /** Room end states: a way to the plaza (a visitor) or back to My Room (the owner). */
  exits: boolean;
}

/** Kick reasons that end a room visit (docs/design/p2-room-visits.md §3). */
const ROOM_ENDS: ReadonlySet<string> = new Set(["room_closed", "removed", "left_room"]);

/** The overlay for every state except "joined" (null: no overlay). In a room the texts name the room. */
export function statusView(status: PlazaStatus, place: "plaza" | "room" = "plaza"): StatusView | null {
  const view = plazaStatusView(status);
  if (view === null || place === "plaza") return view;
  const ended = (status.kind === "kicked" && ROOM_ENDS.has(status.reason)) || (status.kind === "failed" && !status.retry && !status.leave);
  const title = status.kind === "kicked" && status.reason === "room_closed" ? "The room closed" : inRoom(view.title);
  return { ...view, title, text: inRoom(view.text), exits: ended };
}

function plazaStatusView(status: PlazaStatus): StatusView | null {
  const exits = false;
  switch (status.kind) {
    case "joined":
    case "closed":
      return null;
    case "connecting":
      return { title: "Entering the plaza", text: "Entering the plaza…", busy: true, action: null, button: "", exits };
    case "reconnecting":
      return { title: "Reconnecting", text: "The connection was lost. Reconnecting…", busy: true, action: null, button: "", exits };
    case "kicked": {
      const action = status.rule.action;
      if (action.kind === "auto") return { title: "Plaza restarting", text: status.rule.message, busy: true, action: null, button: "", exits };
      if (action.kind === "manual") return { title: "You left the plaza", text: status.rule.message, busy: false, action: "retry", button: status.rule.button ?? "Rejoin", exits };
      if (action.kind === "leave") return { title: "Session ended", text: status.rule.message, busy: false, action: "start", button: "", exits };
      return { title: "You left the plaza", text: status.rule.message, busy: false, action: null, button: "", exits };
    }
    case "failed":
      return {
        title: "The plaza is not available",
        text: status.message,
        busy: false,
        action: status.leave ? "start" : status.retry ? "retry" : null,
        button: "Try again",
        exits,
      };
    case "outdated":
      return { title: "Update available", text: "VYBE has an update. Reload the page to enter the plaza.", busy: false, action: "reload", button: "", exits };
  }
}
