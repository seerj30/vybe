import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { Router } from "@angular/router";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";

type State = "checking" | "ready" | "creating" | "failed";

/**
 * `/play` (spec §3, §2 J1): resume a valid guest session, or create a guest. A failed creation shows
 * a retry and keeps no local identity.
 */
@Component({
  selector: "vybe-onboarding",
  template: `
    <section class="page onboarding">
      <h1>Play as a guest</h1>
      @if (state() === "checking") {
        <p class="muted" role="status">Checking for your guest session…</p>
      } @else {
        @if (lostGuest()) {
          <p class="notice" role="status">
            Your earlier guest session has ended. Guest progress cannot be recovered without a linked account.
          </p>
        }
        <p>
          A guest account keeps your avatar, coins and items in this browser. Clearing cookies or a long break
          ends a guest session, so link an account later to keep your progress.
        </p>
        @if (error(); as error) {
          <p class="error" role="alert">{{ error }}</p>
        }
        <div class="actions">
          <button type="button" class="button" (click)="create()" [disabled]="state() === 'creating'">
            {{ state() === "creating" ? "Creating your guest…" : error() ? "Try again" : "Start as guest" }}
          </button>
        </div>
      }
    </section>
  `,
  styles: `.onboarding { max-width: 560px; padding-top: 12vh; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Onboarding {
  private readonly api = inject(API);
  private readonly player = inject(PlayerStore);
  private readonly router = inject(Router);

  protected readonly state = signal<State>("checking");
  protected readonly error = signal<string | null>(null);
  protected readonly lostGuest = signal(false);

  constructor() {
    void this.check();
  }

  protected async create(): Promise<void> {
    this.state.set("creating");
    this.error.set(null);
    try {
      await this.api.createGuest();
    } catch (error: unknown) {
      if (!isApiError(error, "already_authenticated")) {
        this.error.set(errorMessage(error));
        this.state.set("failed");
        return;
      }
    }
    try {
      await this.player.startedGuest();
    } catch {
      // The guest exists; the next screen loads the player again.
    }
    await this.router.navigateByUrl("/avatar/create?welcome=1");
  }

  private async check(): Promise<void> {
    try {
      if ((await this.player.ensure()) !== null) {
        await this.router.navigateByUrl("/hub");
        return;
      }
      this.lostGuest.set(this.player.hadGuest());
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    }
    this.state.set("ready");
  }
}
