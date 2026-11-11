import { ChangeDetectionStrategy, Component } from "@angular/core";
import { RouterLink } from "@angular/router";
import { GAMES } from "../games/games";

/** `/arcade` (spec §3): choose a game. */
@Component({
  selector: "vybe-arcade",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Arcade</h1>
      <p class="muted">VYBE checks each run and pays V-Coins and XP. Daily quests count runs of every game.</p>
      <div class="games">
        @for (game of games; track game.id) {
          <article class="card">
            <h2>{{ game.name }}</h2>
            <p class="muted">{{ game.blurb }}</p>
            <div class="actions">
              <a class="button" [routerLink]="game.route">Play {{ game.name }}</a>
              <!-- One span: a flex button drops the space before a separate text run. -->
              <a class="button secondary" [routerLink]="game.practiceRoute">
                <span>Practice<span class="visually-hidden"> {{ game.name }}</span> (no rewards)</span>
              </a>
            </div>
          </article>
        }
      </div>
    </section>
  `,
  styles: `
    h2 { margin-top: 0; }
    .games { display: grid; gap: var(--space-4); }
    @media (min-width: 768px) { .games { grid-template-columns: 1fr 1fr; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Arcade {
  protected readonly games = GAMES;
}
