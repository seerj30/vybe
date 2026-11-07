import { Injectable, inject } from "@angular/core";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { AgeGate } from "../player/age-gate";
import { type Me, PlayerStore } from "../player/player-store";
import { AuthSession } from "./auth-session";

/** Which step to send again after a temporary failure. */
export type LinkStep = "resolve" | "link";

/** Result of connecting a Supabase session to a VYBE player (spec §2 J3). */
export type LinkOutcome =
  /** The account's player is active: linked just now, or before. `isNew`: no guest existed, a new player was made. */
  | { kind: "ready"; me: Me; isNew: boolean }
  /** The identity has no player. Linking binds `guest` (or a new guest) to it for good, so the user confirms first. */
  | { kind: "confirm"; guest: Me | null }
  /** The account already has a player and this browser also has another guest: the user picks one, nothing merges. */
  | { kind: "choose"; account: Me; guest: Me }
  /** The identity belongs to another player (409 `identity_already_linked`); nothing merged. */
  | { kind: "conflict" }
  /** Temporary failure (`auth_unavailable`, `rate_limited`, network): the guest is still valid; try `step` again. */
  | { kind: "retry"; step: LinkStep; message: string; retryAfterSeconds: number | null }
  /** The proof was rejected or the step cannot work; the guest is still valid. */
  | { kind: "rejected"; message: string };

/**
 * Connects the signed-in Supabase identity to a player in two steps. `resolve` only reads: an identity
 * with a player signs in to it; an identity without one needs a link. `link` runs only after the user
 * confirmed: it binds the guest of this browser (`POST /accounts/link`, a new guest if there is none),
 * so the player id, wallet and inventory stay the same.
 */
@Injectable({ providedIn: "root" })
export class AccountLink {
  private readonly api = inject(API);
  private readonly accounts = inject(AccountApi);
  private readonly auth = inject(AuthSession);
  private readonly player = inject(PlayerStore);
  private readonly ageGate = inject(AgeGate);
  /** The guest that `prepareSignUp` created in this tab: linking it makes a new player for the account. */
  private signUpGuestId: string | null = null;

  async resolve(): Promise<LinkOutcome> {
    try {
      if ((await this.auth.accessToken()) === null) return { kind: "rejected", message: "Sign in first." };
      const account = await this.accountPlayer();
      const guest = await this.guestPlayer();
      if (account === null) return { kind: "confirm", guest };
      await this.player.refresh();
      return guest !== null && guest.id !== account.id ? { kind: "choose", account, guest } : { kind: "ready", me: account, isNew: false };
    } catch (error: unknown) {
      return outcomeOf(error, "resolve");
    }
  }

  /**
   * Before a Supabase sign-up (spec §17 decision 1): a browser without a guest passes the age gate and gets its
   * guest first, so an under-age answer never creates an Auth user or an email record. `ok: false` with a null
   * message means the user cancelled the gate.
   */
  async prepareSignUp(): Promise<{ ok: true } | { ok: false; message: string | null }> {
    try {
      if ((await this.guestPlayer()) !== null) return { ok: true };
      const birth = await this.ageGate.ask();
      if (birth === null) return { ok: false, message: null };
      try {
        const created = await this.api.createGuest(birth);
        this.signUpGuestId = created.playerId;
      } catch (error: unknown) {
        if (!isApiError(error, "already_authenticated")) throw error;
      }
      await this.player.startedGuest().catch(() => null); // the guest exists; a failed refresh is not a failed gate
      return { ok: true };
    } catch (error: unknown) {
      return { ok: false, message: errorMessage(error) };
    }
  }

  /**
   * Call only after the user confirmed the link of this identity (shown by email). Without a guest (a sign-in
   * of an existing Auth user without a player), the age gate comes first; a cancelled gate goes back to the
   * confirmation. An under-age answer there also deletes that Auth user (server queue) and signs out here.
   */
  async link(): Promise<LinkOutcome> {
    try {
      const token = await this.auth.accessToken();
      if (token === null) return { kind: "rejected", message: "Sign in first." };
      const guest = await this.guestPlayer();
      let isNew = guest !== null && guest.id === this.signUpGuestId;
      if (guest === null) {
        const birth = await this.ageGate.ask();
        if (birth === null) return { kind: "confirm", guest: null };
        try {
          await this.api.createGuest(birth, token);
        } catch (error: unknown) {
          if (!isApiError(error, "age_not_eligible")) throw error;
          await this.auth.signOut();
          await this.player.refresh().catch(() => null);
          return { kind: "rejected", message: errorMessage(error) };
        }
        isNew = true;
      }
      await this.accounts.linkAccount(token);
      const me = await this.player.refresh();
      if (me === null) return { kind: "retry", step: "resolve", message: errorMessage(null), retryAfterSeconds: null };
      return { kind: "ready", me, isNew };
    } catch (error: unknown) {
      return outcomeOf(error, "link");
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

function outcomeOf(error: unknown, step: LinkStep): LinkOutcome {
  if (isApiError(error, "identity_already_linked")) return { kind: "conflict" };
  if (isApiError(error, "invalid_link_proof", "account_suspended")) return { kind: "rejected", message: errorMessage(error) };
  if (isApiError(error, "unauthenticated")) {
    // The guest session ended between the steps; the next attempt starts a new guest.
    return { kind: "retry", step, message: "Your session ended. Try again to continue.", retryAfterSeconds: null };
  }
  if (isApiError(error) && (error.retryable || error.code === "auth_unavailable")) {
    return { kind: "retry", step, message: errorMessage(error), retryAfterSeconds: error.retryAfterSeconds };
  }
  if (isApiError(error)) return { kind: "rejected", message: errorMessage(error) };
  return { kind: "retry", step, message: errorMessage(error), retryAfterSeconds: null };
}
