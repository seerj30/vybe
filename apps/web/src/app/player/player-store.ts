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
  private generation = 0;

  readonly me = this.meSignal.asReadonly();

  /** The player, loaded once; null without a valid guest session or linked account. Other failures throw. */
  ensure(): Promise<Me | null> {
    this.loaded ??= this.load();
    return this.loaded;
  }

  refresh(): Promise<Me | null> {
    this.loaded = this.load();
    return this.loaded;
  }

  /** Forgets the player (sign-out), so no screen shows the old one; the next `ensure()` loads again. */
  clear(): void {
    this.generation++;
    this.loaded = null;
    this.meSignal.set(null);
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

  /** A failed load is forgotten, so the next `ensure()` tries again; only the latest load sets `me`. */
  private load(): Promise<Me | null> {
    const generation = ++this.generation;
    const loading: Promise<Me | null> = this.fetch(generation).catch((error: unknown) => {
      if (this.loaded === loading) this.loaded = null;
      throw error;
    });
    return loading;
  }

  private async fetch(generation: number): Promise<Me | null> {
    let me: Me | null;
    try {
      me = await this.api.getMe();
    } catch (error: unknown) {
      // player_not_found: a Supabase identity without a player yet; it links a guest first (J3).
      if (!isApiError(error, "unauthenticated", "player_not_found")) throw error;
      me = null;
    }
    if (generation === this.generation) this.meSignal.set(me);
    return me;
  }
}

function rememberGuest(): void {
  try {
    localStorage.setItem(GUEST_FLAG, "1");
  } catch {
    // Storage blocked: the lost-guest hint is optional.
  }
}
