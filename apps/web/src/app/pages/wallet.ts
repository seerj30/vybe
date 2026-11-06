import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";
import { LEDGER_LABELS } from "../ui/labels";

type LedgerEntry = Schemas["LedgerEntry"];

/** Wallet ledger (`GET /me/wallet/transactions`), newest first; read-only. */
@Component({
  selector: "vybe-wallet",
  imports: [DatePipe],
  template: `
    <section class="page">
      <h1>Wallet</h1>
      @if (player.me(); as me) {
        <p class="balance"><span data-testid="wallet-balance">{{ me.walletBalance }}</span> V-Coins</p>
      }
      <p class="muted">V-Coins have no money value. You earn them by playing and spend them in the shop.</p>
      @if (error(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button" (click)="load(cursor())">Try again</button>
      }
      @if (loaded() && entries().length === 0) {
        <p class="muted">No V-Coin activity yet. Play Street Hoops to earn your first coins.</p>
      }
      @if (entries().length > 0) {
        <table>
          <caption class="visually-hidden">V-Coin history, newest first</caption>
          <thead>
            <tr><th scope="col">When</th><th scope="col">What</th><th scope="col" class="num">Change</th><th scope="col" class="num">Balance</th></tr>
          </thead>
          <tbody>
            @for (entry of entries(); track entry.id) {
              <tr>
                <td>{{ entry.createdAt | date: "short" }}</td>
                <td>{{ labels[entry.reason] }}</td>
                <td class="num" [class.plus]="entry.delta > 0">{{ entry.delta > 0 ? "+" : "−" }}{{ abs(entry.delta) }}</td>
                <td class="num">{{ entry.balanceAfter }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
      @if (cursor()) {
        <button type="button" class="button secondary" (click)="load(cursor())">Show older entries</button>
      }
    </section>
  `,
  styles: `
    .balance { font-size: 32px; font-weight: 800; color: #ffc83d; margin: 0; font-variant-numeric: tabular-nums; }
    table { width: 100%; border-collapse: collapse; margin: 16px 0; font-size: 15px; }
    th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid #2b3350; }
    th { font-size: 12px; text-transform: uppercase; color: #aab1c8; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    .plus { color: #7be3a1; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Wallet {
  private readonly api = inject(API);
  protected readonly player = inject(PlayerStore);
  protected readonly labels = LEDGER_LABELS;
  protected readonly entries = signal<LedgerEntry[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly abs = Math.abs;

  constructor() {
    void this.player.refresh().catch(() => undefined);
    void this.load(null);
  }

  protected async load(cursor: string | null): Promise<void> {
    this.error.set(null);
    try {
      const page = await this.api.listWalletTransactions({ cursor: cursor ?? undefined });
      this.entries.update((entries) => [...entries, ...page.items]);
      this.cursor.set(page.nextCursor);
      this.loaded.set(true);
    } catch (error: unknown) {
      this.error.set(errorMessage(error));
    }
  }
}
