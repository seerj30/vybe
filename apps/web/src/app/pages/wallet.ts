import { DatePipe } from "@angular/common";
import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";
import { CoinChange } from "../ui/coin-change";
import { LEDGER_LABELS } from "../ui/labels";

type LedgerEntry = Schemas["LedgerEntry"];

/** Wallet ledger (`GET /me/wallet/transactions`), newest first; read-only. */
@Component({
  selector: "vybe-wallet",
  imports: [CoinChange, DatePipe],
  template: `
    <section class="page">
      <h1>Wallet</h1>
      @if (player.me(); as me) {
        <p class="balance"><span data-testid="wallet-balance">{{ me.walletBalance }}</span> V-Coins</p>
      }
      <p class="muted intro">V-Coins have no money value. You earn them by playing and spend them in the shop.</p>
      @if (error(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <div class="actions"><button type="button" class="button" (click)="load(cursor())">Try again</button></div>
      }
      @if (!loaded() && !error()) {
        <p class="muted" role="status">Loading your V-Coin history…</p>
      }
      @if (loaded() && entries().length === 0) {
        <p class="empty">No V-Coin activity yet. Play Street Hoops to earn your first coins.</p>
      }
      @if (entries().length > 0) {
        <div class="table-wrap">
          <table class="table">
            <caption class="visually-hidden">V-Coin history, newest first</caption>
            <thead>
              <tr><th scope="col">When</th><th scope="col">What</th><th scope="col" class="num">Change</th><th scope="col" class="num">Balance</th></tr>
            </thead>
            <tbody>
              @for (entry of entries(); track entry.id) {
                <tr>
                  <td class="when"><span>{{ entry.createdAt | date: "MMM d" }}</span> <span>{{ entry.createdAt | date: "h:mm a" }}</span></td>
                  <td>{{ labels[entry.reason] }}</td>
                  <td class="num"><vybe-coin-change [delta]="entry.delta" /></td>
                  <td class="num">{{ entry.balanceAfter }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
      @if (cursor()) {
        <div class="actions"><button type="button" class="button secondary" (click)="load(cursor())">Show older entries</button></div>
      }
    </section>
  `,
  styles: `
    .balance { font-size: var(--text-3xl); font-weight: 800; color: var(--color-accent); margin: 0; font-variant-numeric: tabular-nums; }
    .intro { margin: var(--space-2) 0 var(--space-5); }
    .when { color: var(--color-text-muted); font-size: var(--text-sm); }
    .when span { white-space: nowrap; }
    @media (max-width: 480px) { .when span { display: block; } }
    @media (max-width: 480px) { .table th, .table td { padding: var(--space-3) var(--space-2); } .table { font-size: var(--text-sm); } }
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
