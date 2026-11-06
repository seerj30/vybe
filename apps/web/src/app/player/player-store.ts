import { Injectable, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";

export type Me = Schemas["Me"];

const GUEST_FLAG = "vybe.hadGuest";

/**
 * The calling player (`GET /me`). Screens call `refresh()` after a purchase, reward or equip,
 * so wallet and level never come from local arithmetic (spec §10).
 */
@Injectable({ providedIn: "root" })
export class PlayerStore {
  private readonly api = inject(API);
  private readonly meSignal = signal<Me | null>(null);
  private loaded: Promise<Me | null> | null = null;

  readonly me = this.meSignal.asReadonly();

  /** The player, loaded once; null when there is no valid guest session. Other failures throw. */
  ensure(): Promise<Me | null> {
    this.loaded ??= this.fetch().catch((error: unknown) => {
      this.loaded = null;
      throw error;
    });
    return this.loaded;
  }

  refresh(): Promise<Me | null> {
    this.loaded = this.fetch();
    return this.loaded;
  }

  /** Called after `createGuest` succeeded. */
  async startedGuest(): Promise<Me | null> {
    rememberGuest();
    return this.refresh();
  }

  /** True when this browser had a guest before, so a missing session means a lost cookie (spec §2 J1). */
  hadGuest(): boolean {
    try {
      return localStorage.getItem(GUEST_FLAG) === "1";
    } catch {
      return false;
    }
  }

  private async fetch(): Promise<Me | null> {
    try {
      const me = await this.api.getMe();
      this.meSignal.set(me);
      return me;
    } catch (error: unknown) {
      if (!isApiError(error, "unauthenticated")) throw error;
      this.meSignal.set(null);
      return null;
    }
  }
}

function rememberGuest(): void {
  try {
    localStorage.setItem(GUEST_FLAG, "1");
  } catch {
    // Storage blocked: the lost-guest hint is optional.
  }
}
