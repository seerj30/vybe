import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import { errorMessage } from "../api/messages";
import { type Me, PlayerStore } from "../player/player-store";

type State = { kind: "loading" } | { kind: "player"; me: Me } | { kind: "new" } | { kind: "unavailable"; message: string };

/** `/` (spec §3): start as guest or continue; `loading` and `unavailable` states. */
@Component({
  selector: "vybe-landing",
  imports: [RouterLink],
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
          </div>
        }
      }
    </section>
  `,
  styles: `
    .landing { padding-top: 18vh; text-align: center; }
    h1 { font-size: clamp(48px, 12vw, 96px); letter-spacing: 0.1em; margin: 0; }
    .tagline { font-size: 18px; color: #c8cde0; }
    .actions { justify-content: center; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Landing {
  private readonly player = inject(PlayerStore);
  protected readonly state = signal<State>({ kind: "loading" });

  constructor() {
    void this.load();
  }

  protected me(): Me | null {
    const state = this.state();
    return state.kind === "player" ? state.me : null;
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
