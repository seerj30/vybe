import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, computed, input } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Me } from "./player-store";

/** Spec §17 decision 2: the hub shows a link prompt when fewer than this many days remain. */
export const LINK_PROMPT_DAYS = 14;

const DAY_MS = 24 * 60 * 60 * 1000;

export interface GuestExpiry {
  expiresAt: Date;
  /** Whole days left, rounded down; 0 on the last day. */
  daysLeft: number;
  /** True when the link prompt shows (fewer than `LINK_PROMPT_DAYS` days left). */
  prompt: boolean;
}

/** The end of the guest session of `me`, or null for a registered player or an unknown expiry. */
export function guestExpiry(me: Pick<Me, "kind" | "guestExpiresAt">, now: Date): GuestExpiry | null {
  if (me.kind !== "guest" || me.guestExpiresAt === null) return null;
  const expiresAt = new Date(me.guestExpiresAt);
  if (Number.isNaN(expiresAt.getTime())) return null;
  const daysLeft = Math.max(0, Math.floor((expiresAt.getTime() - now.getTime()) / DAY_MS));
  return { expiresAt, daysLeft, prompt: expiresAt.getTime() - now.getTime() < LINK_PROMPT_DAYS * DAY_MS };
}

/** Hub card: when the guest session ends, and a link prompt in the last 14 days (spec §2 J1, §17 decision 2). */
@Component({
  selector: "vybe-guest-expiry",
  imports: [DatePipe, RouterLink],
  template: `
    @if (expiry(); as e) {
      @if (e.prompt) {
        <div class="notice" role="status" data-testid="guest-expiry">
          <strong>Your guest progress ends {{ e.daysLeft === 0 ? "today" : e.daysLeft === 1 ? "in 1 day" : "in " + e.daysLeft + " days" }}</strong>
          ({{ e.expiresAt | date: "mediumDate" }}). Link an account to keep your player, coins and items.
          <a routerLink="/settings">Link an account</a>
        </div>
      } @else {
        <p class="muted" data-testid="guest-expiry">
          Guest progress is kept until {{ e.expiresAt | date: "mediumDate" }}. Each visit extends it.
          <a routerLink="/settings">Link an account</a> to keep it for good.
        </p>
      }
    }
  `,
  styles: `p, div { margin: 0; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GuestExpiryNotice {
  readonly me = input.required<Me>();
  protected readonly expiry = computed(() => guestExpiry(this.me(), new Date()));
}
