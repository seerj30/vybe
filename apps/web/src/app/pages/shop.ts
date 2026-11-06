import { ChangeDetectionStrategy, Component, computed, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { ActionKeys, isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";
import { ItemThumb } from "../ui/item-thumb";
import { SLOT_LABELS, SLOTS } from "../ui/labels";

type CatalogItem = Schemas["CatalogItem"];
type ItemSlot = Schemas["ItemSlot"];

/** Per-item purchase state. `unknown`: the answer was lost or delayed, so the result is not known yet. */
type Purchase = { kind: "confirming" } | { kind: "buying" } | { kind: "unknown"; message: string } | { kind: "failed"; message: string };

const LOST = "The connection was lost.";

const PAGE_SIZE = 24;

/**
 * `/shop` (spec §3, §8): catalog with slot filter and purchases. The server decides; the buttons only
 * hint (owned, level, funds). One Idempotency-Key per purchase, reused when the answer was lost.
 */
@Component({
  selector: "vybe-shop",
  imports: [ItemThumb, RouterLink],
  template: `
    <section class="page">
      <h1>Shop</h1>
      @if (player.me(); as me) {
        <p>Balance: <strong>{{ me.walletBalance }} V-Coins</strong>. Purchases are final. Items never change game results.</p>
      }
      <label class="filter">
        Slot
        <select (change)="filter($event)">
          <option value="">All items</option>
          @for (slot of slots; track slot) {
            <option [value]="slot" [selected]="slot === slotFilter()">{{ slotLabels[slot] }}</option>
          }
        </select>
      </label>
      @if (picturesFailed()) {
        <p class="notice" role="status">Some item pictures could not load. Placeholders show instead.</p>
      }
      <p class="sr-status" aria-live="polite">{{ announcement() }}</p>

      @if (loadError(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button" (click)="reload()">Try again</button>
      } @else if (loading() && items().length === 0) {
        <p class="muted" role="status">Loading the shop…</p>
      } @else if (items().length === 0) {
        <p class="muted">No items here right now.</p>
      }

      <ul class="grid">
        @for (item of items(); track item.id) {
          <li class="card item" [attr.aria-label]="item.name">
            <vybe-item-thumb [src]="item.previewUrl" [name]="item.name" (failed)="picturesFailed.set(true)" />
            <h2>{{ item.name }}</h2>
            <p class="muted meta">{{ slotLabels[item.slot] }} · {{ item.rarity }} · level {{ item.minLevel }}+</p>
            <p class="price">{{ item.price }} V-Coins</p>
            @let state = purchases()[item.id];
            @if (item.owned) {
              <p class="owned">Owned · <a routerLink="/wardrobe">Wear it</a></p>
            } @else {
              @switch (state?.kind) {
                @case ("confirming") {
                  <p>Buy {{ item.name }} for {{ item.price }} V-Coins? This cannot be undone.</p>
                  <div class="row">
                    <button type="button" class="button" (click)="buy(item)">Confirm purchase</button>
                    <button type="button" class="button secondary" (click)="cancel(item)">Cancel</button>
                  </div>
                }
                @case ("buying") {
                  <button type="button" class="button" disabled>Buying…</button>
                }
                @case ("unknown") {
                  <p class="error" role="alert">{{ failure(item) }} The purchase may or may not have gone through.</p>
                  <button type="button" class="button" (click)="checkAndRetry(item)">Check and retry</button>
                }
                @default {
                  @if (state?.kind === "failed") {
                    <p class="error" role="alert">{{ failure(item) }}</p>
                  }
                  <button type="button" class="button" [disabled]="blocker(item) !== null" (click)="confirm(item)">
                    {{ blocker(item) ?? "Buy" }}
                  </button>
                }
              }
            }
          </li>
        }
      </ul>
      @if (nextCursor()) {
        <button type="button" class="button secondary" [disabled]="loading()" (click)="loadMore()">Show more items</button>
      }
    </section>
  `,
  styles: `
    .filter { display: inline-flex; gap: 8px; align-items: center; margin-bottom: 12px; }
    select { min-height: 44px; padding: 6px 10px; border-radius: 10px; background: #1b2135; color: inherit; border: 1px solid #2b3350; font: inherit; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 16px; padding: 0; margin: 16px 0; list-style: none; }
    .item { display: flex; flex-direction: column; gap: 6px; }
    .item vybe-item-thumb { width: 112px; }
    .item h2 { font-size: 18px; margin: 6px 0 0; }
    .meta, .price, .owned { margin: 0; }
    .meta { text-transform: capitalize; font-size: 14px; }
    .price { font-weight: 800; color: #ffc83d; }
    .item p { font-size: 14px; }
    .item .button { margin-top: auto; }
    .row { display: flex; gap: 8px; flex-wrap: wrap; }
    .sr-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Shop {
  private readonly api = inject(API);
  protected readonly player = inject(PlayerStore);
  protected readonly slots = SLOTS;
  protected readonly slotLabels = SLOT_LABELS;

  protected readonly slotFilter = signal<ItemSlot | null>(null);
  protected readonly items = signal<CatalogItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly picturesFailed = signal(false);
  protected readonly purchases = signal<Partial<Record<string, Purchase>>>({});
  protected readonly announcement = signal("");
  private readonly keys = new ActionKeys<string>();
  private list = 0;
  private readonly balance = computed(() => this.player.me()?.walletBalance ?? 0);
  private readonly level = computed(() => this.player.me()?.level ?? 1);

  constructor() {
    // Fresh balance and level for the buy hints.
    void this.player.refresh().catch(() => undefined);
    void this.reload();
  }

  /** Why Buy is off, as its label; null when the player may try. The server still decides. */
  protected blocker(item: CatalogItem): string | null {
    if (this.level() < item.minLevel) return `Needs level ${item.minLevel}`;
    if (this.balance() < item.price) return "Not enough V-Coins";
    return null;
  }

  protected failure(item: CatalogItem): string {
    const state = this.purchases()[item.id];
    return state?.kind === "failed" || state?.kind === "unknown" ? state.message : "";
  }

  protected filter(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.slotFilter.set(value === "" ? null : (value as ItemSlot));
    void this.reload();
  }

  protected confirm(item: CatalogItem): void {
    this.setPurchase(item.id, { kind: "confirming" });
  }

  protected cancel(item: CatalogItem): void {
    this.setPurchase(item.id, undefined);
    this.keys.settle(item.id);
  }

  protected async buy(item: CatalogItem): Promise<void> {
    this.setPurchase(item.id, { kind: "buying" });
    try {
      await this.api.purchase({ itemId: item.id }, this.keys.keyFor(item.id));
      this.keys.settle(item.id);
      this.bought(item);
    } catch (error: unknown) {
      this.keys.settle(item.id, error);
      if (isApiError(error, "already_owned")) {
        this.bought(item);
      } else if (isApiError(error) && error.retryable) {
        this.setPurchase(item.id, { kind: "unknown", message: unknownText(error) });
      } else {
        this.setPurchase(item.id, { kind: "failed", message: errorMessage(error) });
      }
    }
    void this.player.refresh().catch(() => undefined);
  }

  /** Spec §2 J1: after a lost answer, read inventory and wallet first, then retry with the same key. */
  protected async checkAndRetry(item: CatalogItem): Promise<void> {
    this.setPurchase(item.id, { kind: "buying" });
    try {
      const [inventory] = await Promise.all([this.api.getInventory(), this.player.refresh()]);
      if (inventory.items.some((owned) => owned.itemId === item.id)) {
        this.keys.settle(item.id);
        this.bought(item);
        return;
      }
    } catch (error: unknown) {
      this.setPurchase(
        item.id,
        isApiError(error) && error.retryable ? { kind: "unknown", message: unknownText(error) } : { kind: "failed", message: errorMessage(error) },
      );
      return;
    }
    await this.buy(item);
  }

  protected async reload(): Promise<void> {
    this.list++;
    this.items.set([]);
    this.nextCursor.set(null);
    await this.loadPage(undefined);
  }

  protected loadMore(): Promise<void> {
    return this.loadPage(this.nextCursor() ?? undefined);
  }

  /** Pages of an older list (another filter) are dropped, so a slow answer never mixes lists. */
  private async loadPage(cursor: string | undefined): Promise<void> {
    const list = this.list;
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const page = await this.api.listCatalog({ slot: this.slotFilter() ?? undefined, cursor, limit: PAGE_SIZE });
      if (list !== this.list) return;
      this.items.update((items) => [...items, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (error: unknown) {
      if (list !== this.list) return;
      this.loadError.set(errorMessage(error));
    }
    this.loading.set(false);
  }

  private bought(item: CatalogItem): void {
    this.items.update((items) => items.map((i) => (i.id === item.id ? { ...i, owned: true } : i)));
    this.setPurchase(item.id, undefined);
    this.announcement.set(`You bought ${item.name}.`);
  }

  private setPurchase(itemId: string, state: Purchase | undefined): void {
    this.purchases.update((purchases) => ({ ...purchases, [itemId]: state }));
  }
}

function unknownText(error: unknown): string {
  return isApiError(error, "network_error") ? LOST : errorMessage(error);
}
