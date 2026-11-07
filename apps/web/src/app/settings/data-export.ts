import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, DestroyRef, type OnInit, inject, input, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";

type DataExport = Schemas["DataExport"];

const POLL_MS = 2000;

/**
 * J4 data export (spec §5): request, follow the status, and download through a short-lived link.
 * The export id is kept per player in this browser (it is not a credential: the API checks the owner).
 */
@Component({
  selector: "vybe-data-export",
  imports: [DatePipe],
  template: `
    <p>Get a copy of your VYBE data as a JSON file: your profile, avatar, items, coins, games and challenges.</p>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (current(); as current) {
      @switch (current.status) {
        @case ("queued") {
          <p class="muted" role="status">Your export is waiting to start…</p>
        }
        @case ("running") {
          <p class="muted" role="status">Your export is being prepared…</p>
        }
        @case ("ready") {
          <p class="notice" role="status">Your export is ready.</p>
          @if (safeUrl(current.downloadUrl); as url) {
            <p>
              <a class="button" [href]="url" download rel="noopener noreferrer">Download your data</a>
            </p>
            <p class="muted">
              This link works until {{ current.downloadExpiresAt | date: "short" }}. After that, get a new link.
            </p>
          } @else {
            <p class="muted">The export has expired. Request a new one.</p>
          }
          <button type="button" class="button secondary" [disabled]="busy()" (click)="refresh()">Get a new download link</button>
        }
        @case ("failed") {
          <p class="error" role="alert">The export did not work. Request a new one.</p>
        }
      }
    }
    @if (canRequest()) {
      <div class="actions">
        <button type="button" class="button" [disabled]="busy()" (click)="request()">Request my data</button>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataExportPanel implements OnInit {
  private readonly api = inject(AccountApi);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  /** The calling player's id: the stored export id belongs to one player. */
  readonly playerId = input.required<string>();

  protected readonly current = signal<DataExport | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer !== null) clearTimeout(this.timer);
    });
  }

  ngOnInit(): void {
    const stored = this.storedId();
    if (stored !== null) void this.follow(stored);
  }

  /** Only https links (http and file only on a local dev host), so API data cannot become a script or data URL. */
  protected safeUrl(url: string | null): string | null {
    return safeDownloadUrl(url, location.hostname);
  }

  protected canRequest(): boolean {
    const status = this.current()?.status;
    return status !== "queued" && status !== "running";
  }

  protected async request(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const { exportId } = await this.api.requestDataExport();
      await this.follow(exportId);
    } catch (error: unknown) {
      if (isApiError(error, "export_in_progress") && error.problem?.exportId !== undefined) {
        await this.follow(error.problem.exportId);
      } else if (isApiError(error, "rate_limited")) {
        this.error.set("You can get one data export every 24 hours. Use the last export, or try again later.");
      } else {
        this.error.set(errorMessage(error));
      }
    } finally {
      this.busy.set(false);
    }
  }

  /** A ready export answers with a new short-lived URL on every read. */
  protected async refresh(): Promise<void> {
    const id = this.current()?.exportId;
    if (id === undefined) return;
    this.busy.set(true);
    try {
      await this.follow(id);
    } finally {
      this.busy.set(false);
    }
  }

  private async follow(exportId: string): Promise<void> {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    try {
      const current = await this.api.getDataExport(exportId);
      if (this.destroyed) return;
      this.error.set(null);
      this.current.set(current);
      this.store(exportId);
      if (current.status === "queued" || current.status === "running") {
        this.timer = setTimeout(() => void this.follow(exportId), POLL_MS);
      }
    } catch (error: unknown) {
      if (this.destroyed) return;
      if (isApiError(error, "not_found")) {
        this.store(null);
        this.current.set(null);
        return;
      }
      this.error.set(errorMessage(error));
      if (isApiError(error) && error.retryable) this.timer = setTimeout(() => void this.follow(exportId), POLL_MS * 2);
    }
  }

  private key(): string {
    return `vybe.export.${this.playerId()}`;
  }

  private storedId(): string | null {
    try {
      return localStorage.getItem(this.key());
    } catch {
      return null;
    }
  }

  private store(exportId: string | null): void {
    try {
      if (exportId === null) localStorage.removeItem(this.key());
      else localStorage.setItem(this.key(), exportId);
    } catch {
      // Storage blocked: the status shows until the page closes.
    }
  }
}

export function safeDownloadUrl(url: string | null, hostname: string): string | null {
  if (url === null) return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  const local = hostname === "localhost" || hostname === "127.0.0.1";
  if (parsed.protocol === "https:" || (local && (parsed.protocol === "http:" || parsed.protocol === "file:"))) return parsed.href;
  return null;
}
