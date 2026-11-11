import { afterNextRender, ChangeDetectionStrategy, Component, type ElementRef, inject, Injector, input, output, signal, viewChild } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import { SOCIAL_API } from "../api/social-api";

type Outfit = Schemas["Outfit"];
type Equipped = Schemas["Equipped"];

export const MAX_OUTFITS = 3;

/** Item ids of an outfit, in slot order, without empty places. */
export function outfitItemIds(equipped: Equipped): string[] {
  const ids = [equipped.hair, equipped.top, equipped.bottom, equipped.shoes, ...equipped.accessories, equipped.emote];
  return ids.filter((id): id is string => id !== null);
}

/**
 * Names of items from the inventory. Items that are not in the inventory any more have no name here,
 * so they are counted: "Starter Tee, Starter Shorts and 1 item you no longer own".
 */
export function itemNames(ids: readonly string[], names: ReadonlyMap<string, string>): string {
  if (ids.length === 0) return "no items";
  const known = ids.flatMap((id) => {
    const name = names.get(id);
    return name === undefined ? [] : [name];
  });
  const unknown = ids.length - known.length;
  const parts = unknown === 0 ? known : [...known, `${unknown} ${unknown === 1 ? "item" : "items"} you no longer own`];
  return parts.length === 1 ? (parts[0] ?? "") : `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}

/**
 * Saved outfits in `/wardrobe` (spec §3, §15 Equip): save the current equipment, apply, delete. Apply is
 * all-or-nothing on the server; an outfit with an item that is no longer valid cannot be applied and
 * names that item (ADR 0005 #6).
 */
@Component({
  selector: "vybe-outfits-panel",
  template: `
    <section class="outfits" aria-labelledby="outfits-title">
      <h2 id="outfits-title" #heading tabindex="-1">Saved outfits</h2>
      <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>
      @if (loadError(); as error) {
        <p class="error" role="alert">Saved outfits could not load. {{ error }}</p>
        <button type="button" class="button secondary" (click)="load()">Try again</button>
      } @else if (outfits(); as outfits) {
        @if (outfits.length === 0) {
          <p class="empty">No saved outfits. Save what you wear now to switch back to it later.</p>
        }
        <ul>
          @for (outfit of outfits; track outfit.id) {
            <li class="card outfit" [attr.aria-label]="outfit.name">
              <h3>{{ outfit.name }}</h3>
              <p class="muted small">{{ names(ids(outfit)) }}</p>
              @if (!outfit.valid) {
                <p class="notice small">
                  You cannot wear this outfit now. Not available: {{ names(outfit.invalidItemIds) }}.
                  An item is not available when you do not own it, it is withdrawn, or it does not fit your body.
                  Delete the outfit or save a new one.
                </p>
              }
              <div class="row">
                <button type="button" class="button" [disabled]="busy() || locked() || !outfit.valid" (click)="apply(outfit)">
                  Wear<span class="visually-hidden"> outfit {{ outfit.name }}</span>
                </button>
                <button type="button" class="button secondary" [disabled]="busy() || locked()" (click)="remove(outfit)">
                  Delete<span class="visually-hidden"> outfit {{ outfit.name }}</span>
                </button>
              </div>
            </li>
          }
        </ul>
        @if (outfits.length < max) {
          <form class="save" (submit)="save($event)">
            <label>
              <span>Outfit name</span>
              <input type="text" maxlength="40" required [value]="name()" (input)="name.set(inputValue($event))" />
            </label>
            <button type="submit" class="button" [disabled]="busy() || locked() || name().trim() === ''">Save current outfit</button>
          </form>
        } @else {
          <p class="muted small">You can save {{ max }} outfits. Delete one to save a new one.</p>
        }
      } @else {
        <p class="muted" role="status">Loading saved outfits…</p>
      }
    </section>
  `,
  styles: `
    h2 { margin: var(--space-5) 0 var(--space-2); }
    h2:focus { outline: none; }
    ul { display: grid; gap: var(--space-3); padding: 0; margin: 0 0 12px; list-style: none; }
    h3 { margin: 0 0 4px; font-size: var(--text-md); }
    .small { font-size: var(--text-xs); margin: 0 0 8px; }
    .row { display: flex; flex-wrap: wrap; gap: var(--space-2); }
    .save { display: flex; flex-wrap: wrap; gap: var(--space-2); align-items: end; }
    .save label { display: grid; gap: var(--space-1); flex: 1 1 200px; font-weight: 600; }
    p:empty { display: none; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OutfitsPanel {
  /** The current avatar revision, for the compare-and-swap on apply. */
  readonly revision = input.required<number>();
  /** Item names by id, from the inventory. */
  readonly itemNames = input.required<ReadonlyMap<string, string>>();
  /** True while the wardrobe equips an item. */
  readonly locked = input(false);
  /** The new equipment after a successful apply. */
  readonly applied = output<Schemas["EquipmentUpdated"]>();
  /** The avatar or the inventory changed elsewhere (409, 422): the wardrobe reloads them. */
  readonly stale = output<void>();
  /** True while an outfit action runs, so the wardrobe does not equip with the same revision at the same time. */
  readonly working = output<boolean>();

  private readonly social = inject(SOCIAL_API);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLElement>>("heading");
  protected readonly max = MAX_OUTFITS;
  protected readonly outfits = signal<Outfit[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly name = signal("");

  constructor() {
    void this.load();
  }

  protected ids(outfit: Outfit): string[] {
    return outfitItemIds(outfit.equipped);
  }

  protected names(ids: readonly string[]): string {
    return itemNames(ids, this.itemNames());
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.outfits.set((await this.social.listOutfits()).outfits);
    } catch (error: unknown) {
      this.loadError.set(errorMessage(error));
    }
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const name = this.name().trim();
    if (name === "") return;
    await this.act(async () => {
      await this.social.saveOutfit(name);
      this.name.set("");
      return `Outfit "${name}" saved.`;
    });
  }

  protected async apply(outfit: Outfit): Promise<void> {
    await this.act(async () => {
      this.applied.emit(await this.social.applyOutfit(outfit.id, this.revision()));
      return `You now wear the outfit "${outfit.name}".`;
    });
  }

  protected async remove(outfit: Outfit): Promise<void> {
    await this.act(async () => {
      await this.social.deleteOutfit(outfit.id);
      return `Outfit "${outfit.name}" deleted.`;
    });
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** Runs one action, then reads the list again: validity and the slot count come from the server. */
  private async act(action: () => Promise<string>): Promise<void> {
    this.busy.set(true);
    this.working.emit(true);
    this.message.set(null);
    try {
      this.message.set({ text: await action(), error: false });
    } catch (error: unknown) {
      this.message.set({ text: errorMessage(error), error: true });
      // Another tab changed the avatar, or the inventory changed: the wardrobe reads both again.
      if (isApiError(error, "revision_conflict", "outfit_items_invalid")) this.stale.emit();
    }
    await this.load();
    this.busy.set(false);
    this.working.emit(false);
    // A deleted outfit takes its buttons with it: focus continues at the panel heading, the message follows it.
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }
}
