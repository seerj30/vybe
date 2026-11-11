import { DatePipe, DecimalPipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { errorMessage } from "../api/messages";
import { ADMIN_FORM_STYLES, inputText } from "./admin-form";
import { FlagsApi, type ReviewFlag } from "./flags-api";

type FlaggedRun = Schemas["FlaggedRun"];

/** Moderator wording of each flag (spec §17 decision 4 starting thresholds). */
export const FLAG_LABELS: Record<ReviewFlag, string> = {
  perfect_rate_high: "Too many perfect shots",
  gap_variance_low: "Even shot timing",
  aim_variance_low: "Even aim and power",
  daily_cap_streak: "Daily run cap 7 days in a row",
  timing_spread_low: "Even touch timing",
};

/**
 * Flagged runs, newest first (moderator). Flags are signals only: this panel takes no action. A moderator who
 * decides to act uses the suspension panel, which asks for a reason and writes the audit log.
 */
@Component({
  selector: "vybe-admin-flags",
  imports: [DatePipe, DecimalPipe],
  template: `
    <p class="muted">Signals only. A flag never changes a run or a reward. To act, use Suspensions with a reason.</p>
    <label class="filter">
      Show
      <select (change)="filter(inputText($event))" aria-label="Flag filter">
        <option value="" selected>All flags</option>
        @for (flag of flags; track flag) {
          <option [value]="flag">{{ labels[flag] }}</option>
        }
      </select>
    </label>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loaded() && runs().length === 0) {
      <p class="muted">No flagged runs.</p>
    }
    @if (runs().length > 0) {
      <div class="table-wrap">
      <table class="table" data-testid="flagged-runs">
        <caption class="visually-hidden">Flagged runs, newest first</caption>
        <thead>
          <tr><th scope="col">Player</th><th scope="col">Flags</th><th scope="col">Score</th><th scope="col">Run time</th><th scope="col">Submitted</th></tr>
        </thead>
        <tbody>
          @for (run of runs(); track run.sessionId) {
            <tr [attr.aria-label]="'Flagged run of ' + run.player.handle">
              <td>
                <strong>{{ run.player.handle }}</strong><br />
                <span class="muted">Player id</span> <code>{{ run.player.playerId }}</code>
              </td>
              <td>
                <ul class="flags">
                  @for (flag of run.flags; track flag) {
                    <li>{{ labels[flag] }}</li>
                  }
                </ul>
              </td>
              <td>{{ run.score | number }}<br /><span class="muted">{{ run.eventCount }} shots</span></td>
              <td>{{ run.runMs / 1000 | number: "1.1-1" }} s</td>
              <td>
                {{ run.submittedAt | date: "short" }}
                @if (run.challengeId) {
                  <br /><span class="muted">Challenge run</span>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
      </div>
    }
    @if (cursor()) {
      <button type="button" class="button secondary" [disabled]="loading()" (click)="load(cursor())">
        {{ loading() ? "Loading…" : "Show older runs" }}
      </button>
    }
  `,
  styles: [
    ADMIN_FORM_STYLES,
    `
      .table-wrap { margin-top: var(--space-4); }
      .table { font-size: var(--text-sm); }
      .table th, .table td { vertical-align: top; overflow-wrap: anywhere; }
      .filter { max-width: 280px; margin-bottom: 12px; }
      .flags { margin: 0; padding-left: 18px; }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminFlags {
  private readonly api = inject(FlagsApi);
  protected readonly inputText = inputText;
  protected readonly labels = FLAG_LABELS;
  protected readonly flags = Object.keys(FLAG_LABELS) as ReviewFlag[];
  protected readonly runs = signal<FlaggedRun[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  /** True while a page request is open: "Show older runs" sends at most one request at a time. */
  protected readonly loading = signal(false);
  private flag: ReviewFlag | undefined;
  /** Changes with each filter; an answer for an older filter is dropped. */
  private generation = 0;

  constructor() {
    void this.load(null);
  }

  protected filter(value: string): void {
    this.flag = value === "" ? undefined : (value as ReviewFlag);
    this.generation++;
    this.runs.set([]);
    this.cursor.set(null);
    this.loaded.set(false);
    void this.load(null);
  }

  protected async load(cursor: string | null): Promise<void> {
    if (cursor !== null && this.loading()) return; // a second click while the older page loads
    this.error.set(null);
    this.loading.set(true);
    const generation = this.generation;
    try {
      const page = await this.api.list(this.flag, cursor ?? undefined);
      if (generation !== this.generation) return; // the filter changed meanwhile
      this.runs.update((runs) => [...runs, ...page.items]);
      this.cursor.set(page.nextCursor);
      this.loaded.set(true);
    } catch (error: unknown) {
      if (generation === this.generation) this.error.set(errorMessage(error));
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }
}
