import { Injectable, inject } from "@angular/core";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { type Me, PlayerStore } from "../player/player-store";
import { AuthSession } from "./auth-session";

/** Result of connecting a Supabase session to a VYBE player (spec §2 J3). */
export type LinkOutcome =
  /** The account's player is active: linked just now, or before. `isNew`: no guest existed, a new player was made. */
  | { kind: "ready"; me: Me; isNew: boolean }
  /** The account already has a player and this browser also has another guest: the user picks one, nothing merges. */
  | { kind: "choose"; account: Me; guest: Me }
  /** The identity belongs to another player (409 `identity_already_linked`); nothing merged. */
  | { kind: "conflict" }
  /** Temporary failure (`auth_unavailable`, `rate_limited`, network): the guest is still valid; try again. */
  | { kind: "retry"; message: string; retryAfterSeconds: number | null }
  /** The proof was rejected or the step cannot work; the guest is still valid. */
  | { kind: "rejected"; message: string };

/**
 * Connects the signed-in Supabase identity to a player. An identity with a player signs in to it.
 * An identity without a player links the guest of this browser (`POST /accounts/link`, a new guest if
 * there is none), so the player id, wallet and inventory stay the same.
 */
@Injectable({ providedIn: "root" })
export class AccountLink {
  private readonly api = inject(API);
  private readonly accounts = inject(AccountApi);
  private readonly auth = inject(AuthSession);
  private readonly player = inject(PlayerStore);

  async connect(): Promise<LinkOutcome> {
    try {
      const token = await this.auth.accessToken();
      if (token === null) return { kind: "rejected", message: "Sign in first." };

      const account = await this.accountPlayer();
      const guest = await this.guestPlayer();
      if (account !== null) {
        await this.player.refresh();
        return guest !== null && guest.id !== account.id ? { kind: "choose", account, guest } : { kind: "ready", me: account, isNew: false };
      }

      let isNew = false;
      if (guest === null) {
        await this.api.createGuest();
        isNew = true;
      }
      await this.accounts.linkAccount(token);
      const me = await this.player.refresh();
      if (me === null) return { kind: "retry", message: errorMessage(null), retryAfterSeconds: null };
      return { kind: "ready", me, isNew };
    } catch (error: unknown) {
      return outcomeOf(error);
    }
  }

  /** The player of the bearer token; null for an identity without a player. */
  private async accountPlayer(): Promise<Me | null> {
    try {
      return await this.api.getMe();
    } catch (error: unknown) {
      if (isApiError(error, "player_not_found")) return null;
      throw error;
    }
  }

  /** The guest of the cookie; null without a valid guest session. */
  private async guestPlayer(): Promise<Me | null> {
    try {
      const me = await this.accounts.getGuest();
      return me.kind === "guest" ? me : null;
    } catch (error: unknown) {
      if (isApiError(error, "unauthenticated")) return null;
      throw error;
    }
  }
}

function outcomeOf(error: unknown): LinkOutcome {
  if (isApiError(error, "identity_already_linked")) return { kind: "conflict" };
  if (isApiError(error, "invalid_link_proof", "account_suspended")) return { kind: "rejected", message: errorMessage(error) };
  if (isApiError(error, "unauthenticated")) {
    // The guest session ended between the steps; the next attempt starts a new guest.
    return { kind: "retry", message: "Your guest session ended. Try again to continue.", retryAfterSeconds: null };
  }
  if (isApiError(error) && (error.retryable || error.code === "auth_unavailable")) {
    return { kind: "retry", message: errorMessage(error), retryAfterSeconds: error.retryAfterSeconds };
  }
  if (isApiError(error)) return { kind: "rejected", message: errorMessage(error) };
  return { kind: "retry", message: errorMessage(error), retryAfterSeconds: null };
}
