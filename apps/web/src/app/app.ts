import { ChangeDetectionStrategy, Component } from "@angular/core";
import { StreetHoops } from "./street-hoops/street-hoops";

@Component({
  selector: "vybe-root",
  imports: [StreetHoops],
  // Lazy chunk: Three.js and the simulator load after the shell (spec §10).
  template: `
    @defer {
      <vybe-street-hoops />
    } @loading {
      <p class="loading">Loading Street Hoops…</p>
    } @error {
      <p class="loading" role="alert">Street Hoops could not load. Reload the page.</p>
    }
  `,
  styles: `.loading { color: #f4f6ff; font: 16px system-ui, sans-serif; text-align: center; margin-top: 40vh; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
