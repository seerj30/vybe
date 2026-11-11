import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { AccountApi } from "../api/account-api";
import { ActionKeys, isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { CoinChange } from "../ui/coin-change";
import { ADMIN_FORM_STYLES, inputText, validReason } from "./admin-form";

const MAX_DELTA = 1_000_000;

/**
 * Wallet adjustment with an audited reason (administrator only; spec §5, ADR 0005 #10). One
 * Idempotency-Key per adjustment (player, change, reason): a retry after a lost answer never adjusts twice.
 */
@Component({
  selector: "vybe-admin-wallet",
  imports: [CoinChange],
  template: `
    <form (submit)="$event.preventDefault(); adjust()" aria-label="Wallet adjustment">
      <label>
        Player id
        <input type="text" name="playerId" autocomplete="off" [value]="playerId()" (input)="playerId.set(inputText($event).trim())" />
      </label>
      <label>
        Change in V-Coins <span class="hint">(negative to remove, at most 1,000,000)</span>
        <input type="number" name="delta" step="1" [min]="-max" [max]="max" [value]="delta()" (input)="delta.set(inputText($event))" />
      </label>
      <label>
        Reason
        <input type="text" name="reason" minlength="3" maxlength="500" [value]="reason()" (input)="reason.set(inputText($event))" />
      </label>
      <div class="actions">
        <button type="submit" class="button" [disabled]="!ready()">Adjust wallet</button>
      </div>
    </form>
    @if (error(); as error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (result(); as result) {
      <p class="notice" role="status">
        Wallet of {{ result.playerId }} changed by <vybe-coin-change [delta]="result.delta" />. New balance:
        {{ result.balanceAfter }} V-Coins.
      </p>
    }
  `,
  styles: ADMIN_FORM_STYLES,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminWallet {
  private readonly api = inject(AccountApi);
  private readonly keys = new ActionKeys<string>();
  protected readonly inputText = inputText;
  protected readonly max = MAX_DELTA;
  protected readonly playerId = signal("");
  protected readonly delta = signal("");
  protected readonly reason = signal("");
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<Schemas["WalletAdjustment"] | null>(null);

  protected ready(): boolean {
    return !this.busy() && this.playerId() !== "" && this.parsedDelta() !== null && validReason(this.reason());
  }

  protected async adjust(): Promise<void> {
    const delta = this.parsedDelta();
    if (delta === null) return;
    const body = { playerId: this.playerId(), delta, reason: this.reason().trim() };
    const action = JSON.stringify(body);
    this.busy.set(true);
    this.error.set(null);
    this.result.set(null);
    try {
      this.result.set(await this.api.adminAdjustWallet(body, this.keys.keyFor(action)));
      this.keys.settle(action);
      this.delta.set("");
      this.reason.set("");
    } catch (error: unknown) {
      this.keys.settle(action, error);
      this.error.set(
        isApiError(error, "insufficient_funds")
          ? "The balance cannot go below 0. Nothing changed."
          : isApiError(error, "not_found")
            ? "VYBE has no player with this id."
            : errorMessage(error),
      );
    } finally {
      this.busy.set(false);
    }
  }

  private parsedDelta(): number | null {
    const text = this.delta().trim();
    if (!/^-?\d+$/.test(text)) return null;
    const value = Number(text);
    return value !== 0 && Math.abs(value) <= MAX_DELTA ? value : null;
  }
}
