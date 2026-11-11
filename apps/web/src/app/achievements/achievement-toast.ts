import { ChangeDetectionStrategy, Component, input } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { TIER_LABELS } from "./achievement-view";

/**
 * After a verified run: the achievements that this run unlocked (P1, docs/design/p1-achievements.md). A polite live
 * region, so a screen reader reads it after "Run verified". Shows nothing when the run unlocked nothing.
 */
@Component({
  selector: "vybe-achievement-toast",
  imports: [RouterLink],
  template: `
    @if (unlocked().length > 0) {
      <div class="toast" role="status" data-testid="achievement-toast">
        <ul>
          @for (a of unlocked(); track a.code) {
            <li>
              <span class="medal" [attr.data-tier]="a.tier" aria-hidden="true">★</span>
              <span>Achievement unlocked: <strong>{{ a.name }}</strong>
                <span class="muted">({{ tier[a.tier] }}, +{{ a.rewardXp }} XP)</span></span>
            </li>
          }
        </ul>
        <a routerLink="/achievements">See your achievements</a>
      </div>
    }
  `,
  styles: `
    .toast { margin: 12px 0; padding: 12px 16px; border-radius: 14px; background: var(--color-highlight); border: 2px solid var(--color-accent); animation: pop 240ms ease-out; }
    ul { list-style: none; margin: 0 0 8px; padding: 0; display: grid; gap: 6px; }
    li { display: flex; gap: var(--space-2); align-items: baseline; }
    .medal { font-size: var(--text-lg); color: var(--color-bronze); }
    .medal[data-tier="silver"] { color: var(--color-silver); }
    .medal[data-tier="gold"] { color: var(--color-gold); }
    @keyframes pop { from { transform: scale(0.96); opacity: 0; } to { transform: none; opacity: 1; } }
    @media (prefers-reduced-motion: reduce) { .toast { animation: none; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AchievementToast {
  readonly unlocked = input.required<readonly Schemas["AchievementUnlocked"][]>();
  protected readonly tier = TIER_LABELS;
}
