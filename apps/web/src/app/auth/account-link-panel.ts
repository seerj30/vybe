import { ChangeDetectionStrategy, Component, inject, output, signal } from "@angular/core";
import { type Me, PlayerStore } from "../player/player-store";
import { AccountLink, type LinkOutcome } from "./account-link";
import { AuthSession } from "./auth-session";
import { SignInForm } from "./sign-in-form";

type PanelState = { kind: "idle" } | { kind: "connecting" } | LinkOutcome;

export interface LinkDone {
  me: Me;
  isNew: boolean;
  /** False when the user kept the guest and signed out. */
  signedIn: boolean;
}

/**
 * J3 in the UI: sign in (or create an account), then connect the identity to a player. The guest
 * progress is linked, never merged; conflicts and temporary failures get their own states.
 */
@Component({
  selector: "vybe-account-link",
  imports: [SignInForm],
  template: `
    @switch (auth.status()) {
      @case ("loading") {
        <p class="muted" role="status">Loading sign-in…</p>
      }
      @case ("off") {
        <p class="notice" role="status">Sign-in is not available in this version of VYBE.</p>
      }
      @case ("unavailable") {
        <p class="notice" role="status">Sign-in is not available right now. Try again later.</p>
      }
      @default {
        @switch (state().kind) {
          @case ("idle") {
            @if (auth.status() === "signed-out") {
              <vybe-sign-in-form (signedIn)="connect()" />
            }
          }
          @case ("connecting") {
            <p class="muted" role="status">Connecting your account…</p>
          }
          @case ("ready") {
            <p class="notice" role="status">Signed in. Your progress is saved to your account.</p>
          }
          @case ("choose") {
            @if (choice(); as choice) {
              <div class="notice" role="alert">
                <p>
                  This account already has the VYBE player <strong>{{ choice.account.handle }}</strong>. Your guest
                  <strong>{{ choice.guest.handle }}</strong> is a different player. VYBE does not merge players, so pick one.
                </p>
              </div>
              <div class="actions">
                <button type="button" class="button" (click)="finish(choice.account, false)">Play as {{ choice.account.handle }}</button>
                <button type="button" class="button secondary" (click)="keepGuest()">Keep playing as {{ choice.guest.handle }}</button>
              </div>
            }
          }
          @case ("conflict") {
            <p class="error" role="alert">
              This account already belongs to another VYBE player. Nothing was merged, and your guest progress is safe.
              Sign out and use another account, or contact support to choose the player.
            </p>
            <button type="button" class="button secondary" (click)="signOut()">Sign out</button>
          }
          @case ("retry") {
            <p class="error" role="alert">{{ message() }}</p>
            <button type="button" class="button" (click)="connect()">Try again</button>
          }
          @case ("rejected") {
            <p class="error" role="alert">{{ message() }}</p>
            <button type="button" class="button secondary" (click)="signOut()">Sign out</button>
          }
        }
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountLinkPanel {
  protected readonly auth = inject(AuthSession);
  private readonly link = inject(AccountLink);
  private readonly player = inject(PlayerStore);

  /** The player to continue with, after a link, a sign-in or a choice. */
  readonly done = output<LinkDone>();
  protected readonly state = signal<PanelState>({ kind: "idle" });

  constructor() {
    // Back from the email confirmation link, or signed in on another screen: connect at once.
    void this.auth.ready().then(() => {
      if (this.auth.status() === "signed-in" && this.state().kind === "idle") void this.connect();
    });
  }

  protected choice(): { account: Me; guest: Me } | null {
    const state = this.state();
    return state.kind === "choose" ? state : null;
  }

  protected message(): string {
    const state = this.state();
    if (state.kind === "retry") {
      return state.retryAfterSeconds === null ? state.message : `${state.message} (wait ${state.retryAfterSeconds} s)`;
    }
    return state.kind === "rejected" ? state.message : "";
  }

  protected async connect(): Promise<void> {
    this.state.set({ kind: "connecting" });
    const outcome = await this.link.connect();
    this.state.set(outcome);
    if (outcome.kind === "ready") this.done.emit({ me: outcome.me, isNew: outcome.isNew, signedIn: true });
  }

  protected finish(me: Me, isNew: boolean): void {
    this.done.emit({ me, isNew, signedIn: true });
  }

  protected async keepGuest(): Promise<void> {
    await this.auth.signOut();
    const me = await this.player.refresh().catch(() => null);
    this.state.set({ kind: "idle" });
    if (me !== null) this.done.emit({ me, isNew: false, signedIn: false });
  }

  protected async signOut(): Promise<void> {
    await this.auth.signOut();
    await this.player.refresh().catch(() => null);
    this.state.set({ kind: "idle" });
  }
}
