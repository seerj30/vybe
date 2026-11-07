import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi, type AdminReportStatus } from "../api/account-api";
import { errorMessage } from "../api/messages";
import { ADMIN_FORM_STYLES, inputText, validReason } from "./admin-form";

type Report = Schemas["AdminReport"];

const REASONS: Record<Schemas["ReportReason"], string> = {
  offensive_name: "Offensive name",
  cheating: "Cheating",
  harassment: "Harassment",
  other: "Other",
};

/** Report queue, oldest first (moderator; spec §5 Admin, ADR 0005 #10). */
@Component({
  selector: "vybe-admin-reports",
  imports: [DatePipe],
  template: `
    <label class="filter">
      Show
      <select (change)="filter(inputText($event))">
        <option value="open" selected>Open reports</option>
        <option value="actioned">Actioned reports</option>
        <option value="dismissed">Dismissed reports</option>
        <option value="">All reports</option>
      </select>
    </label>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (notice(); as notice) {
      <p class="notice" role="status">{{ notice }}</p>
    }
    @if (loaded() && reports().length === 0) {
      <p class="muted">No reports.</p>
    }
    <ul class="reports">
      @for (report of reports(); track report.reportId) {
        <li class="card" [attr.aria-label]="'Report on ' + report.target.handle">
          <p>
            <strong>{{ report.target.handle }}</strong> reported by {{ report.reporter.handle }} ·
            {{ reasons[report.reason] }} · {{ report.createdAt | date: "short" }}
          </p>
          <p class="muted">Target player id: <code>{{ report.target.playerId }}</code></p>
          @if (report.details) {
            <p class="details">{{ report.details }}</p>
          }
          @if (report.status === "open") {
            <form (submit)="$event.preventDefault()">
              <label>
                Resolution reason
                <input type="text" minlength="3" maxlength="500" [value]="reasonFor(report.reportId)" (input)="setReason(report.reportId, inputText($event))" />
              </label>
              <div class="actions">
                <button type="button" class="button" [disabled]="!canResolve(report.reportId)" (click)="resolve(report, 'actioned')">Mark as actioned</button>
                <button type="button" class="button secondary" [disabled]="!canResolve(report.reportId)" (click)="resolve(report, 'dismissed')">Dismiss</button>
              </div>
            </form>
          } @else {
            <p class="muted">{{ report.status === "actioned" ? "Actioned" : "Dismissed" }} {{ report.reviewedAt | date: "short" }}</p>
          }
        </li>
      }
    </ul>
    @if (cursor()) {
      <button type="button" class="button secondary" (click)="load(cursor())">Show more</button>
    }
  `,
  styles: [
    ADMIN_FORM_STYLES,
    `
      .filter { max-width: 240px; margin-bottom: 12px; }
      .reports { list-style: none; padding: 0; display: grid; gap: 12px; }
      .reports p { margin: 0 0 8px; }
      .details { white-space: pre-wrap; overflow-wrap: anywhere; }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminReports {
  private readonly api = inject(AccountApi);
  protected readonly reasons = REASONS;
  protected readonly inputText = inputText;
  protected readonly reports = signal<Report[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  private readonly resolutionReasons = signal<Record<string, string>>({});
  private readonly busy = signal<string | null>(null);
  private status: AdminReportStatus | undefined = "open";

  constructor() {
    void this.load(null);
  }

  protected filter(value: string): void {
    this.status = value === "" ? undefined : (value as AdminReportStatus);
    this.reports.set([]);
    this.loaded.set(false);
    void this.load(null);
  }

  protected reasonFor(reportId: string): string {
    return this.resolutionReasons()[reportId] ?? "";
  }

  protected setReason(reportId: string, reason: string): void {
    this.resolutionReasons.update((reasons) => ({ ...reasons, [reportId]: reason }));
  }

  protected canResolve(reportId: string): boolean {
    return this.busy() !== reportId && validReason(this.reasonFor(reportId));
  }

  protected async load(cursor: string | null): Promise<void> {
    this.error.set(null);
    const status = this.status;
    try {
      const page = await this.api.adminListReports(status, cursor ?? undefined);
      if (status !== this.status) return; // the filter changed meanwhile
      this.reports.update((reports) => [...reports, ...page.items]);
      this.cursor.set(page.nextCursor);
      this.loaded.set(true);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    }
  }

  protected async resolve(report: Report, status: "actioned" | "dismissed"): Promise<void> {
    this.busy.set(report.reportId);
    this.error.set(null);
    this.notice.set(null);
    try {
      const resolved = await this.api.adminResolveReport(report.reportId, { status, reason: this.reasonFor(report.reportId).trim() });
      this.reports.update((reports) =>
        this.status === "open" ? reports.filter((r) => r.reportId !== resolved.reportId) : reports.map((r) => (r.reportId === resolved.reportId ? resolved : r)),
      );
      this.notice.set(`Report on ${report.target.handle} ${status === "actioned" ? "marked as actioned" : "dismissed"}.`);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(null);
    }
  }
}
