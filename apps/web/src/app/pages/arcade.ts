import { ChangeDetectionStrategy, Component } from "@angular/core";
import { RouterLink } from "@angular/router";

/** `/arcade` (spec §3): choose a game. */
@Component({
  selector: "vybe-arcade",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Arcade</h1>
      <article class="card">
        <h2>Street Hoops</h2>
        <p class="muted">Aim, charge and release. Hit the moving basket as often as you can in 60 seconds.</p>
        <div class="actions">
          <a class="button secondary" routerLink="/practice">Practice (no rewards)</a>
        </div>
      </article>
    </section>
  `,
  styles: `h2 { margin-top: 0; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Arcade {}
