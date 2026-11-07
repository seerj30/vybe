import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, DestroyRef, type OnInit, inject, input, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";

const POLL_MS = 3000;

/**
 * `/deletion/:requestId` (spec §2 J4): the status of a deletion request. It needs no session, because
 * the deletion revokes the sessions; the random request id in the address is the access key.
 */
@Component({
  selector: "vybe-deletion-status",
  imports: [DatePipe, RouterLink],
  template: `
    <section class="page deletion">
      <h1>Account deletion</h1>
      @if (status(); as status) {
        @switch (status.status) {
          @case ("queued") {
            <p role="status" data-testid="deletion-status">Your deletion request is in the queue.</p>
          }
          @case ("processing") {
            <p role="status" data-testid="deletion-status">VYBE is deleting your account now.</p>
          }
          @case ("completed") {
            <p role="status" data-testid="deletion-status">
              Your account is deleted ({{ status.completedAt | date: "short" }}).
            </p>
            <p class="muted">Backups can keep data for a limited time, as the privacy notice describes.</p>
          }
        }
        <p class="muted">Requested {{ status.createdAt | date: "short" }}. Keep this page address to check the status later.</p>
      } @else if (missing()) {
        <p class="error" role="alert">VYBE has no deletion request with this id.</p>
      } @else if (!error()) {
        <p class="muted" role="status">Loading the status…</p>
      }
      @if (error(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button secondary" (click)="load()">Try again</button>
      }
      <div class="actions">
        <a class="button secondary" routerLink="/">Back to VYBE</a>
      </div>
    </section>
  `,
  styles: `.deletion { max-width: 560px; padding-top: 12vh; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeletionStatus implements OnInit {
  private readonly api = inject(AccountApi);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  readonly requestId = input.required<string>();

  protected readonly status = signal<Schemas["DeletionRequest"] | null>(null);
  protected readonly missing = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer !== null) clearTimeout(this.timer);
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.error.set(null);
    try {
      const status = await this.api.getDeletionRequest(this.requestId());
      if (this.destroyed) return;
      this.status.set(status);
      if (status.status !== "completed") this.timer = setTimeout(() => void this.load(), POLL_MS);
    } catch (error: unknown) {
      if (this.destroyed) return;
      if (isApiError(error, "not_found", "validation_failed", "malformed_request")) this.missing.set(true);
      else this.error.set(errorMessage(error));
    }
  }
}
