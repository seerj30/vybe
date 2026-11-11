import { DatePipe, JsonPipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { errorMessage } from "../api/messages";
import { ADMIN_FORM_STYLES, inputText } from "./admin-form";

type Entry = Schemas["AuditLogEntry"];
interface Filter {
  entityType?: string;
  entityId?: string;
  actor?: string;
}

/** Audit log search, newest first, exact filters (administrator; spec §5, §12). Read-only. */
@Component({
  selector: "vybe-admin-audit",
  imports: [DatePipe, JsonPipe],
  template: `
    <form class="filters" (submit)="$event.preventDefault(); search()" aria-label="Audit log filters">
      <label>Entity type <input type="text" name="entityType" maxlength="64" [value]="entityType()" (input)="entityType.set(inputText($event))" /></label>
      <label>Entity id <input type="text" name="entityId" maxlength="128" [value]="entityId()" (input)="entityId.set(inputText($event))" /></label>
      <label>Actor <input type="text" name="actor" maxlength="256" [value]="actor()" (input)="actor.set(inputText($event))" /></label>
      <div class="actions"><button type="submit" class="button secondary">Search the audit log</button></div>
    </form>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loaded() && entries().length === 0) {
      <p class="muted">No audit log entries.</p>
    }
    @if (entries().length > 0) {
      <div class="table-wrap">
      <table class="table">
        <caption class="visually-hidden">Audit log, newest first</caption>
        <thead>
          <tr><th scope="col">When</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col">Entity</th><th scope="col">Reason</th></tr>
        </thead>
        <tbody>
          @for (entry of entries(); track entry.id) {
            <tr>
              <td>{{ entry.createdAt | date: "short" }}</td>
              <td>{{ entry.actorRole }}<br /><code>{{ entry.actorSubject }}</code></td>
              <td>{{ entry.action }}</td>
              <td>{{ entry.entityType }}<br /><code>{{ entry.entityId }}</code></td>
              <td>
                {{ entry.reason }}
                <details>
                  <summary>Before and after</summary>
                  <pre>{{ entry.before | json }}</pre>
                  <pre>{{ entry.after | json }}</pre>
                </details>
              </td>
            </tr>
          }
        </tbody>
      </table>
      </div>
    }
    @if (cursor()) {
      <button type="button" class="button secondary" (click)="load(cursor())">Show older entries</button>
    }
  `,
  styles: [
    ADMIN_FORM_STYLES,
    `
      .table-wrap { margin-top: var(--space-4); }
      .table { font-size: var(--text-sm); }
      .table th, .table td { vertical-align: top; overflow-wrap: anywhere; }
      .filters { max-width: none; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); align-items: end; }
      .filters .actions { margin: 0; }
      pre { white-space: pre-wrap; font-size: 12px; }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminAudit {
  private readonly api = inject(AccountApi);
  protected readonly inputText = inputText;
  protected readonly entityType = signal("");
  protected readonly entityId = signal("");
  protected readonly actor = signal("");
  protected readonly entries = signal<Entry[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  private filter: Filter = {};

  constructor() {
    void this.load(null);
  }

  protected search(): void {
    this.filter = { entityType: value(this.entityType()), entityId: value(this.entityId()), actor: value(this.actor()) };
    this.entries.set([]);
    this.loaded.set(false);
    void this.load(null);
  }

  protected async load(cursor: string | null): Promise<void> {
    this.error.set(null);
    const filter = this.filter;
    try {
      const page = await this.api.adminListAuditLogs(filter, cursor ?? undefined);
      if (filter !== this.filter) return; // a newer search started
      this.entries.update((entries) => [...entries, ...page.items]);
      this.cursor.set(page.nextCursor);
      this.loaded.set(true);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    }
  }
}

function value(text: string): string | undefined {
  const trimmed = text.trim();
  return trimmed === "" ? undefined : trimmed;
}
