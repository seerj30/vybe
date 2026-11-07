import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { ADMIN_FORM_STYLES, inputText, validReason } from "./admin-form";

/** Suspend and lift (moderator; spec §5 Admin). Without an end time the suspension is indefinite. */
@Component({
  selector: "vybe-admin-suspensions",
  imports: [DatePipe],
  template: `
    <form (submit)="$event.preventDefault(); suspend()" aria-label="Suspension">
      <label>
        Player id
        <input type="text" name="playerId" autocomplete="off" [value]="playerId()" (input)="playerId.set(inputText($event).trim())" />
      </label>
      <label>
        Reason
        <input type="text" name="reason" minlength="3" maxlength="500" [value]="reason()" (input)="reason.set(inputText($event))" />
      </label>
      <label>
        Suspended until <span class="hint">(optional; empty means no end)</span>
        <input type="datetime-local" name="until" [value]="until()" (input)="until.set(inputText($event))" />
      </label>
      <div class="actions">
        <button type="submit" class="button" [disabled]="!ready()">Suspend player</button>
        <button type="button" class="button secondary" [disabled]="!ready()" (click)="lift()">Lift suspension</button>
      </div>
    </form>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (result(); as result) {
      <p class="notice" role="status">
        @if (result.suspended) {
          Player {{ result.playerId }} is suspended{{ result.suspendedUntil ? " until " + (result.suspendedUntil | date: "short") : " with no end" }}.
        } @else {
          Player {{ result.playerId }} is not suspended.
        }
      </p>
    }
  `,
  styles: ADMIN_FORM_STYLES,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminSuspensions {
  private readonly api = inject(AccountApi);
  protected readonly inputText = inputText;
  protected readonly playerId = signal("");
  protected readonly reason = signal("");
  protected readonly until = signal("");
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<Schemas["Suspension"] | null>(null);

  protected ready(): boolean {
    return !this.busy() && this.playerId() !== "" && validReason(this.reason());
  }

  protected suspend(): Promise<void> {
    const until = this.until() === "" ? undefined : new Date(this.until());
    if (until !== undefined && (Number.isNaN(until.getTime()) || until.getTime() <= Date.now())) {
      this.error.set("Pick an end time in the future, or leave it empty.");
      return Promise.resolve();
    }
    return this.run(() =>
      this.api.adminSuspend(this.playerId(), { reason: this.reason().trim(), ...(until === undefined ? {} : { until: until.toISOString() }) }),
    );
  }

  protected lift(): Promise<void> {
    return this.run(() => this.api.adminLiftSuspension(this.playerId(), { reason: this.reason().trim() }));
  }

  private async run(action: () => Promise<Schemas["Suspension"]>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.result.set(null);
    try {
      this.result.set(await action());
      this.reason.set("");
    } catch (error: unknown) {
      this.error.set(isApiError(error, "not_found") ? "VYBE has no active player with this id." : errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
