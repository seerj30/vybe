import { ChangeDetectionStrategy, Component, computed, inject, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { AvatarPortrait } from "../avatar/avatar-portrait";
import { pickPresets, presetLabel } from "../avatar/presets";
import { ItemThumb } from "../ui/item-thumb";
import { SLOT_LABELS, SLOTS } from "../ui/labels";

type Avatar = Schemas["Avatar"];
type InventoryItem = Schemas["InventoryItem"];
type ItemSlot = Schemas["ItemSlot"];

interface SlotGroup {
  slot: ItemSlot;
  items: InventoryItem[];
}

/** Where an item can go: one place per slot, two attachment points for accessories (spec §9). */
interface Place {
  attachIndex?: 0 | 1;
  label: string;
}

const SINGLE: readonly Place[] = [{ label: "" }];
const ACCESSORY: readonly Place[] = [
  { attachIndex: 0, label: "point 1" },
  { attachIndex: 1, label: "point 2" },
];

/**
 * `/wardrobe` (spec §3): owned items per slot; equip and unequip with the avatar revision.
 * Items that do not fit the body preset are shown but cannot be equipped.
 */
@Component({
  selector: "vybe-wardrobe",
  imports: [AvatarPortrait, ItemThumb, RouterLink],
  template: `
    <section class="page">
      <h1>Wardrobe</h1>
      @if (loadError(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button" (click)="load()">Try again</button>
      } @else if (avatar(); as avatar) {
        <div class="layout">
          <div class="card preview">
            <vybe-avatar-portrait [presets]="presets()!" />
            <p class="muted small">{{ bodyLabel() }}</p>
          </div>
          <div>
            @if (picturesFailed()) {
              <p class="notice" role="status">Some item pictures could not load. Placeholders show instead. What you wear did not change.</p>
            }
            <p aria-live="polite" [class.error]="message()?.error" [class.notice]="message() && !message()?.error">{{ message()?.text }}</p>
            @if (groups().length === 0) {
              <p class="muted">You own no items yet. <a routerLink="/shop">Visit the shop</a>.</p>
            }
            @for (group of groups(); track group.slot) {
              <section class="slot" [attr.aria-labelledby]="'slot-' + group.slot">
                <h2 [id]="'slot-' + group.slot">{{ slotLabels[group.slot] }}</h2>
                <ul>
                  @for (item of group.items; track item.itemId) {
                    <li class="card item">
                      <vybe-item-thumb [src]="item.previewUrl" [name]="item.name" (failed)="picturesFailed.set(true)" />
                      <div class="info">
                        <h3>{{ item.name }}</h3>
                        @if (!fits(item)) {
                          <p class="muted">Does not fit {{ bodyLabel() }}.</p>
                        }
                        <div class="row">
                          @for (place of places(group.slot); track place.label) {
                            @if (isWorn(item, place)) {
                              <button type="button" class="button secondary" [disabled]="busy()" (click)="equip(group.slot, null, place)">
                                Take off{{ place.label ? " (" + place.label + ")" : "" }}
                                <span class="visually-hidden">{{ item.name }}</span>
                              </button>
                            } @else {
                              <button type="button" class="button" [disabled]="busy() || !fits(item)" (click)="equip(group.slot, item, place)">
                                Wear{{ place.label ? " on " + place.label : "" }}
                                <span class="visually-hidden">{{ item.name }}</span>
                              </button>
                            }
                          }
                        </div>
                        @if (wornAnywhere(item)) {
                          <p class="worn">Wearing</p>
                        }
                      </div>
                    </li>
                  }
                </ul>
              </section>
            }
          </div>
        </div>
      } @else {
        <p class="muted" role="status">Loading your wardrobe…</p>
      }
    </section>
  `,
  styles: `
    .layout { display: grid; gap: 16px; }
    .preview { display: flex; flex-direction: column; align-items: center; }
    .preview vybe-avatar-portrait { width: 160px; }
    .small { font-size: 13px; margin-bottom: 0; }
    .slot h2 { font-size: 18px; margin: 16px 0 8px; }
    ul { display: grid; gap: 12px; padding: 0; margin: 0; list-style: none; }
    .item { display: grid; grid-template-columns: 72px 1fr; gap: 12px; align-items: center; }
    .item h3 { margin: 0 0 4px; font-size: 16px; }
    .info p { margin: 0 0 6px; font-size: 14px; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; }
    .worn { color: #7be3a1; font-weight: 700; margin-top: 6px !important; }
    p:empty { display: none; }
    @media (min-width: 768px) { .layout { grid-template-columns: 220px 1fr; align-items: start; } .preview { position: sticky; top: 24px; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Wardrobe {
  private readonly api = inject(API);
  protected readonly slotLabels = SLOT_LABELS;
  protected readonly avatar = signal<Avatar | null>(null);
  protected readonly inventory = signal<InventoryItem[]>([]);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly picturesFailed = signal(false);

  protected readonly presets = computed(() => {
    const avatar = this.avatar();
    return avatar === null ? null : pickPresets(avatar);
  });
  protected readonly bodyLabel = computed(() => presetLabel("bodyPresetId", this.avatar()?.bodyPresetId ?? ""));
  protected readonly groups = computed((): SlotGroup[] =>
    SLOTS.map((slot) => ({ slot, items: this.inventory().filter((item) => item.slot === slot) })).filter((g) => g.items.length > 0),
  );

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      const [avatar, inventory] = await Promise.all([this.api.getAvatar(), this.api.getInventory()]);
      this.avatar.set(avatar);
      this.inventory.set(inventory.items);
    } catch (error: unknown) {
      this.loadError.set(errorMessage(error));
    }
  }

  protected places(slot: ItemSlot): readonly Place[] {
    return slot === "accessory" ? ACCESSORY : SINGLE;
  }

  protected fits(item: InventoryItem): boolean {
    const body = this.avatar()?.bodyPresetId;
    return body !== undefined && item.compatibleBodyPresets.includes(body);
  }

  protected isWorn(item: InventoryItem, place: Place): boolean {
    const equipped = this.avatar()?.equipped;
    if (equipped === undefined) return false;
    if (item.slot === "accessory") return equipped.accessories[place.attachIndex ?? 0] === item.itemId;
    return equipped[item.slot] === item.itemId;
  }

  protected wornAnywhere(item: InventoryItem): boolean {
    return this.places(item.slot).some((place) => this.isWorn(item, place));
  }

  protected async equip(slot: ItemSlot, item: InventoryItem | null, place: Place): Promise<void> {
    const avatar = this.avatar();
    if (avatar === null) return;
    this.busy.set(true);
    this.message.set(null);
    try {
      const updated = await this.api.equip({
        slot,
        itemId: item?.itemId ?? null,
        expectedAvatarRevision: avatar.revision,
        ...(place.attachIndex === undefined ? {} : { attachIndex: place.attachIndex }),
      });
      this.avatar.set({ ...avatar, revision: updated.revision, equipped: updated.equipped });
      this.message.set({ text: item === null ? `${SLOT_LABELS[slot]} taken off.` : `You now wear ${item.name}.`, error: false });
    } catch (error: unknown) {
      this.message.set({ text: errorMessage(error), error: true });
      // Another tab changed the avatar: show its current state, so the next try uses the new revision.
      if (isApiError(error, "revision_conflict")) await this.load();
    }
    this.busy.set(false);
  }
}
