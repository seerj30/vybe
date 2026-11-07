import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { Router, RouterLink } from "@angular/router";
import { errorMessage } from "../api/messages";
import { AccountLinkPanel, type LinkDone } from "../auth/account-link-panel";
import { AuthSession } from "../auth/auth-session";
import { type Me, PlayerStore } from "../player/player-store";

type State = { kind: "loading" } | { kind: "player"; me: Me } | { kind: "new" } | { kind: "unavailable"; message: string };

/** `/` (spec §3): start as guest, continue, or sign in (J3 links the guest); `loading` and `unavailable` states. */
@Component({
  selector: "vybe-landing",
  imports: [RouterLink, AccountLinkPanel],
  template: `
    <section class="page landing">
      <h1>VYBE</h1>
      <p class="tagline">Quick arcade games, your own avatar, and friendly challenges. No downloads.</p>
      @switch (state().kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading…</p>
        }
        @case ("unavailable") {
          <p class="error" role="alert">VYBE is not available right now. {{ message() }}</p>
          <div class="actions">
            <button type="button" class="button" (click)="load()">Try again</button>
            <a class="button secondary" routerLink="/practice">Practice Street Hoops offline</a>
          </div>
        }
        @default {
          <div class="actions">
            @if (me(); as me) {
              <a class="button" routerLink="/hub">Continue as {{ me.handle }}</a>
            } @else {
              <a class="button" routerLink="/play">Start as guest</a>
            }
            <a class="button secondary" routerLink="/practice">Practice Street Hoops</a>
            @if (canSignIn() && !signingIn()) {
              <button type="button" class="button secondary" (click)="signingIn.set(true)">Sign in</button>
            }
          </div>
          @if (signingIn() || (auth.status() === "signed-in" && me() === null)) {
            <section class="card sign-in" aria-labelledby="sign-in-title">
              <h2 id="sign-in-title">Sign in to VYBE</h2>
              <p class="muted">Sign in or create an account. A guest in this browser keeps its progress when it links.</p>
              <vybe-account-link (done)="continue($event)" />
            </section>
          }
        }
      }
    </section>
  `,
  styles: `
    .landing { padding-top: 18vh; text-align: center; }
    h1 { font-size: clamp(48px, 12vw, 96px); letter-spacing: 0.1em; margin: 0; }
    .tagline { font-size: 18px; color: #c8cde0; }
    .actions { justify-content: center; }
    .sign-in { max-width: 480px; margin: 24px auto 0; text-align: left; }
    .sign-in h2 { margin-top: 0; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Landing {
  private readonly player = inject(PlayerStore);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthSession);
  protected readonly state = signal<State>({ kind: "loading" });
  protected readonly signingIn = signal(false);

  constructor() {
    void this.load();
  }

  protected me(): Me | null {
    const state = this.state();
    return state.kind === "player" ? state.me : null;
  }

  /** A signed-in player has nothing to sign in to; everyone else may sign in or link. */
  protected canSignIn(): boolean {
    return this.auth.status() !== "off" && this.me()?.kind !== "registered";
  }

  protected async continue(done: LinkDone): Promise<void> {
    await this.router.navigateByUrl(done.isNew ? "/avatar/create?welcome=1" : "/hub");
  }

  protected message(): string {
    const state = this.state();
    return state.kind === "unavailable" ? state.message : "";
  }

  protected async load(): Promise<void> {
    this.state.set({ kind: "loading" });
    try {
      const me = await this.player.ensure();
      this.state.set(me === null ? { kind: "new" } : { kind: "player", me });
    } catch (error: unknown) {
      this.state.set({ kind: "unavailable", message: errorMessage(error) });
    }
  }
}
