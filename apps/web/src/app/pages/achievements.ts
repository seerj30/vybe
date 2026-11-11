import { ChangeDetectionStrategy, Component, computed, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";
import {
  type AchievementFilter,
  categoryLabel,
  FILTERS,
  groupByCategory,
  progressText,
  stateOf,
  TIER_LABELS,
  unlockedText,
} from "../achievements/achievement-view";
import { Generations } from "../challenges/generations";
import { PlayerStore } from "../player/player-store";

type Load = { kind: "loading" } | { kind: "failed"; message: string } | { kind: "ready"; list: Schemas["AchievementList"] };

/**
 * `/achievements` (P1, docs/design/p1-achievements.md): every achievement with the server-computed progress, grouped by
 * category, with a filter. Reached from the hub and the own profile; no main-nav item (the phone bar is full).
 */
@Component({
  selector: "vybe-achievements",
  imports: [RouterLink],
  template: `
    <section class="page">
      <h1>Achievements</h1>
      <p class="muted">VYBE checks every run and action on the server. Each achievement gives a badge on your profile and XP once.</p>

      @let l = state();
      @switch (l.kind) {
        @case ("loading") {
          <p class="muted" role="status">Loading your achievements…</p>
        }
        @case ("failed") {
          <p class="error" role="alert">{{ l.message }}</p>
          <button type="button" class="button" (click)="load()">Try again</button>
        }
        @case ("ready") {
          <div class="card summary" data-testid="achievement-summary">
            <p class="count"><strong>{{ l.list.unlockedCount }}</strong> of {{ l.list.totalCount }} unlocked</p>
            <p class="muted">{{ l.list.earnedXp }} XP from achievements</p>
            @if (player.me(); as me) {
              <a [routerLink]="['/profile', me.handle]">See your badges on your profile</a>
            }
          </div>

          <div class="filters" role="group" aria-label="Show">
            @for (f of filters; track f.value) {
              <button type="button" class="chip" [class.active]="filter() === f.value" [attr.aria-pressed]="filter() === f.value"
                (click)="filter.set(f.value)">{{ f.label }}</button>
            }
          </div>

          @for (group of groups(); track group.category) {
            <section class="group" [attr.aria-labelledby]="'cat-' + group.category">
              <h2 [id]="'cat-' + group.category">{{ category(group.category) }}</h2>
              <ul class="list">
                @for (a of group.items; track a.code) {
                  @let s = itemState(a);
                  <li class="card item" [attr.data-achievement]="a.code" [attr.data-state]="s">
                    <span class="medal" [attr.data-tier]="a.tier" [class.dim]="s !== 'unlocked'" aria-hidden="true">★</span>
                    <div class="body">
                      <h3>{{ a.name }}</h3>
                      <p class="desc">{{ a.description }}</p>
                      @if (s === "unlocked") {
                        <p class="done">Unlocked {{ date(a.unlockedAt!) }} · {{ tier[a.tier] }} · +{{ a.rewardXp }} XP</p>
                      } @else {
                        <div class="bar" role="progressbar" [attr.aria-valuenow]="a.progress" aria-valuemin="0" [attr.aria-valuemax]="a.target"
                          [attr.aria-label]="a.name + ' progress'">
                          <span [style.width.%]="(a.progress / a.target) * 100"></span>
                        </div>
                        <p class="muted small">{{ progress(a) }} · {{ tier[a.tier] }} · {{ a.rewardXp }} XP</p>
                      }
                    </div>
                  </li>
                }
              </ul>
            </section>
          } @empty {
            <p class="muted" role="status">No achievements in this view.</p>
          }
        }
      }
    </section>
  `,
  styles: `
    .summary { display: grid; gap: var(--space-1); margin-bottom: 16px; }
    .summary p { margin: 0; }
    .count { font-size: var(--text-xl); }
    .filters { display: flex; flex-wrap: wrap; gap: var(--space-2); margin-bottom: 8px; }
    h2 { margin: var(--space-5) 0 var(--space-3); }
    .list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--space-3); grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); }
    .item { display: flex; gap: var(--space-3); align-items: flex-start; margin: 0; }
    .medal { flex: none; width: 44px; height: 44px; display: grid; place-items: center; border-radius: 50%; font-size: 24px; background: var(--color-bg); color: var(--color-bronze); }
    .medal[data-tier="silver"] { color: var(--color-silver); }
    .medal[data-tier="gold"] { color: var(--color-gold); }
    .medal.dim { opacity: 0.4; }
    .body { min-width: 0; flex: 1; }
    h3 { margin: 0; font-size: var(--text-md); }
    .desc { margin: 2px 0 8px; }
    .done { margin: 0; color: var(--color-success); font-weight: 600; }
    .bar { height: 8px; border-radius: 4px; background: var(--color-bg); overflow: hidden; margin-bottom: 4px; }
    .bar span { display: block; height: 100%; background: var(--color-link); }
    .small { font-size: var(--text-sm); margin: 0; }
    [data-state="locked"] .bar span { background: transparent; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Achievements {
  private readonly social = inject(SOCIAL_API);
  private readonly loads = new Generations();
  protected readonly player = inject(PlayerStore);
  protected readonly state = signal<Load>({ kind: "loading" });
  protected readonly filter = signal<AchievementFilter>("all");
  protected readonly filters = FILTERS;
  protected readonly tier = TIER_LABELS;
  protected readonly category = categoryLabel;
  protected readonly progress = progressText;
  protected readonly date = unlockedText;
  protected readonly itemState = stateOf;
  protected readonly groups = computed(() => {
    const state = this.state();
    return state.kind === "ready" ? groupByCategory(state.list.achievements, this.filter()) : [];
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    const isCurrent = this.loads.next();
    this.state.set({ kind: "loading" });
    let state: Load;
    try {
      state = { kind: "ready", list: await this.social.listAchievements() };
    } catch (error: unknown) {
      state = { kind: "failed", message: errorMessage(error) };
    }
    if (isCurrent()) this.state.set(state);
  }
}
