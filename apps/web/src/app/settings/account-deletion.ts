import { ChangeDetectionStrategy, Component, inject, input, signal } from "@angular/core";
import { Router } from "@angular/router";
import { AccountApi } from "../api/account-api";
import { isApiError, newIdempotencyKey } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { AuthSession } from "../auth/auth-session";
import { PlayerStore } from "../player/player-store";

/** Remembers the last deletion request id in this browser, so the status page stays reachable. */
export const DELETION_REQUEST_KEY = "vybe.deletionRequest";

type Step = "start" | "confirm" | "verify" | "sending";

/**
 * J4 deletion request (spec §2 J4, §5; ADR 0005 #8). A registered player must have signed in within
 * the last 5 minutes; else the API answers 403 `reauthentication_required` ("verification required"),
 * and the player enters the password again. The Idempotency-Key is the request id and is kept for retries.
 */
@Component({
  selector: "vybe-account-deletion",
  template: `
    <p>
      Deleting your account removes your name, avatar, items, outfits and blocks. VYBE keeps the V-Coin history and
      game records under an anonymous id, as the privacy notice describes. You cannot undo this.
    </p>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @switch (step()) {
      @case ("start") {
        <button type="button" class="button danger" (click)="step.set('confirm')">Delete my account…</button>
      }
      @case ("confirm") {
        <form (submit)="$event.preventDefault(); send()">
          <label class="check">
            <input type="checkbox" [checked]="understood()" (change)="understood.set(checked($event))" />
            I understand that VYBE deletes my account and that I cannot undo this.
          </label>
          <div class="actions">
            <button type="submit" class="button danger" [disabled]="!understood()">Delete my account</button>
            <button type="button" class="button secondary" (click)="cancel()">Cancel</button>
          </div>
        </form>
      }
      @case ("verify") {
        <form (submit)="$event.preventDefault(); verify()" aria-labelledby="verify-title">
          <h3 id="verify-title">Verification required</h3>
          <p>For your safety, enter the password of {{ auth.user()?.email }} again to confirm that it is you.</p>
          <label>
            Password
            <input type="password" name="password" autocomplete="current-password" required [value]="password()" (input)="password.set(text($event))" />
          </label>
          <div class="actions">
            <button type="submit" class="button danger" [disabled]="password() === ''">Confirm and delete</button>
            <button type="button" class="button secondary" (click)="cancel()">Cancel</button>
          </div>
        </form>
      }
      @case ("sending") {
        <p class="muted" role="status">Sending your deletion request…</p>
      }
    }
  `,
  styles: `
    .check { display: flex; gap: 10px; align-items: flex-start; }
    .check input { width: 20px; height: 20px; margin-top: 2px; }
    label { display: grid; gap: 4px; font-weight: 600; max-width: 420px; }
    input[type="password"] { min-height: 44px; padding: 8px 12px; border-radius: 10px; border: 1px solid #3a4468; background: #101421; color: inherit; font: inherit; }
    .button.danger { background: #ff6b6b; color: #141826; }
    h3 { margin-bottom: 4px; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountDeletion {
  private readonly api = inject(AccountApi);
  private readonly player = inject(PlayerStore);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthSession);
  private key: string | null = null;

  readonly registered = input.required<boolean>();

  protected readonly step = signal<Step>("start");
  protected readonly understood = signal(false);
  protected readonly password = signal("");
  protected readonly error = signal<string | null>(null);

  protected cancel(): void {
    this.step.set("start");
    this.understood.set(false);
    this.password.set("");
    this.error.set(null);
  }

  protected async verify(): Promise<void> {
    this.error.set(null);
    this.step.set("sending");
    const result = await this.auth.reauthenticate(this.password());
    this.password.set("");
    if (!result.ok) {
      this.error.set(result.message);
      this.step.set("verify");
      return;
    }
    await this.send();
  }

  protected async send(): Promise<void> {
    this.error.set(null);
    this.step.set("sending");
    this.key ??= newIdempotencyKey();
    try {
      const { requestId } = await this.api.requestDeletion(this.key);
      remember(requestId);
      await this.leave(requestId);
    } catch (error: unknown) {
      if (isApiError(error, "reauthentication_required")) {
        this.step.set("verify");
        return;
      }
      if (!(isApiError(error) && error.retryable)) this.key = null;
      this.error.set(errorMessage(error));
      this.step.set("confirm");
    }
  }

  /** The request exists: always reach the status page, even when the sign-out fails. */
  private async leave(requestId: string): Promise<void> {
    try {
      // The API revoked the guest cookie; the Supabase session ends here.
      if (this.registered()) await this.auth.signOut();
      await this.player.refresh();
    } catch (error: unknown) {
      console.error("Sign-out after the deletion request failed", error);
    }
    await this.router.navigateByUrl(`/deletion/${encodeURIComponent(requestId)}`);
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}

function remember(requestId: string): void {
  try {
    localStorage.setItem(DELETION_REQUEST_KEY, requestId);
  } catch {
    // Storage blocked: the status page address still has the id.
  }
}
