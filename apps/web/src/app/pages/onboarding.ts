import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { Router, RouterLink } from "@angular/router";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { AgeGateForm, type BirthDate } from "../player/age-gate";
import { PlayerStore } from "../player/player-store";

type State = "checking" | "ready" | "creating" | "failed" | "under-age";

/**
 * `/play` (spec §3, §2 J1): resume a valid guest session, or pass the neutral age gate (spec §17 decision 1)
 * and create a guest. A failed creation shows a retry and keeps no local identity. An under-age answer
 * creates nothing and shows a short explanation, with no way to try another year on this page.
 */
@Component({
  selector: "vybe-onboarding",
  imports: [AgeGateForm, RouterLink],
  template: `
    <section class="page onboarding">
      <h1>Play as a guest</h1>
      @switch (state()) {
        @case ("checking") {
          <p class="muted" role="status">Checking for your guest session…</p>
        }
        @case ("under-age") {
          <div class="notice" role="alert" data-testid="age-not-eligible">
            <p><strong>VYBE is only for adults.</strong></p>
            <p>We did not create a player for you, we set no cookie, and we did not keep your answer.</p>
          </div>
          <div class="actions"><a class="button secondary" routerLink="/">Back to the start page</a></div>
        }
        @default {
          @if (lostGuest()) {
            <p class="notice" role="status">
              Your earlier guest session has ended. Guest progress cannot be recovered without a linked account.
            </p>
          }
          <p>
            A guest account keeps your avatar, coins and items in this browser. Clearing cookies or a break of 30 days
            ends a guest session, so link an account later to keep your progress.
          </p>
          @if (error(); as error) {
            <p class="error" role="alert">{{ error }}</p>
          }
          <vybe-age-gate
            [busy]="state() === 'creating'"
            [submitLabel]="error() ? 'Try again' : 'Start as guest'"
            (submitted)="create($event)"
          />
        }
      }
    </section>
  `,
  styles: `.onboarding { max-width: 560px; padding-top: 12vh; } .notice p { margin: 0; }`,
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

  protected async create(birth: BirthDate): Promise<void> {
    this.state.set("creating");
    this.error.set(null);
    try {
      await this.api.createGuest(birth);
    } catch (error: unknown) {
      if (isApiError(error, "age_not_eligible")) {
        this.state.set("under-age");
        return;
      }
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
