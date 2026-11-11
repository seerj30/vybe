import { ChangeDetectionStrategy, Component, type ElementRef, Injector, afterNextRender, computed, inject, signal, viewChild } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { Schemas } from "@vybe/contracts";
import { isApiError } from "../api/api-client";
import { API } from "../api/api";
import { errorMessage } from "../api/messages";
import { itemPicture } from "../avatar/asset-manifest";
import {
  describePlacement,
  EMPTY_ROOM,
  firstFree,
  fromApi,
  isWall,
  type ItemRef,
  type LayoutProblem,
  moved,
  otherWall,
  type Placement,
  type RoomState,
  rotated,
  sameRoom,
  toRequest,
  withPlacement,
} from "../room/room-layout";
import { ROOM, roomItem } from "../room/room-manifest";
import type { Pick } from "../room/room-renderer";
import { RoomViewport } from "../room/room-viewport";
import { ItemThumb } from "../ui/item-thumb";

type InventoryItem = Schemas["InventoryItem"];

interface Message {
  text: string;
  error: boolean;
}

interface Move {
  dx: number;
  dy: number;
  label: string;
  key: string;
}

const PROBLEMS: Record<LayoutProblem, string> = {
  placement_out_of_bounds: "It does not fit there: the room ends.",
  placement_overlap: "Another item is already there.",
  incompatible_item: "This item cannot go there.",
  room_item_limit: `A room holds at most ${ROOM.maxItems} items.`,
};

/**
 * `/room` (docs/design/p1-my-room.md §4): place owned furniture and decor on the grid, then save the whole room.
 * Every action has a button and a key; the 3D view is decorative. The server validates the saved layout again.
 */
@Component({
  selector: "vybe-room",
  imports: [ItemThumb, RoomViewport, RouterLink],
  template: `
    <section class="page" (keydown)="key($event)">
      <h1>My Room</h1>
      <p class="muted">Place your furniture and decor, then save. Players who can see your profile also see your room.</p>
      @if (loadError(); as error) {
        <p class="error" role="alert">{{ error }}</p>
        <button type="button" class="button" (click)="load()">Try again</button>
      } @else if (!loaded()) {
        <p class="muted" role="status">Loading your room…</p>
      } @else {
        <div class="layout">
          <div class="card stage" #stage>
            <vybe-room-viewport [room]="draft()" [selected]="selectedId()" [editable]="true" (picked)="moveTo($event)" />
            <p class="muted small">Select an item, then tap a place in the room. Or use the buttons and the arrow keys.</p>
          </div>

          <div class="side">
            <div class="card save">
              <div class="row">
                <button type="button" class="button" [disabled]="!dirty() || saving()" (click)="save()">
                  {{ saving() ? "Saving…" : "Save room" }}
                </button>
                <button type="button" class="button secondary" [disabled]="!dirty() || saving()" (click)="discard()">Discard changes</button>
              </div>
              @if (dirty()) {
                <p class="notice small">You have unsaved changes.</p>
              }
              @if (message(); as m) {
                <p [class.error]="m.error" [class.notice]="!m.error" [attr.role]="m.error ? 'alert' : 'status'">{{ m.text }}</p>
              }
              @if (conflict()) {
                <button type="button" class="button secondary" (click)="load()">Load the saved room</button>
              }
              <p class="sr-status" aria-live="polite">{{ announcement() }}</p>
            </div>

            @if (selected(); as p) {
              <section class="card controls" aria-labelledby="selected-title">
                <h2 id="selected-title" tabindex="-1" #selectedTitle>{{ nameOf(p.sku) }}</h2>
                <p class="muted small">{{ where(p) }}</p>
                <div class="pad">
                  @for (m of moves(); track m.key) {
                    <button type="button" class="pad-button" (click)="move(m.dx, m.dy)">{{ m.label }}</button>
                  }
                </div>
                <div class="row">
                  @if (p.surface === "floor") {
                    <button type="button" class="button secondary" (click)="rotate()">Turn 90°</button>
                  } @else {
                    <button type="button" class="button secondary" (click)="switchWall()">
                      Move to the {{ p.surface === "wall_back" ? "left" : "back" }} wall
                    </button>
                  }
                  <button type="button" class="button secondary" (click)="remove()">Remove from room</button>
                </div>
                <p class="muted small">Keys: arrows move, R turns, Delete removes, Escape ends the selection.</p>
              </section>
            }

            <section class="card" aria-labelledby="finish-title">
              <h2 id="finish-title">Walls and floor</h2>
              <label class="field">
                Walls
                <select (change)="setFinish('wall', $event)">
                  <option value="" [selected]="draft().wallItem === null">Default walls</option>
                  @for (item of wallFinishes(); track item.itemId) {
                    <option [value]="item.itemId" [selected]="draft().wallItem?.itemId === item.itemId">{{ item.name }}</option>
                  }
                </select>
              </label>
              <label class="field">
                Floor
                <select (change)="setFinish('floor', $event)">
                  <option value="" [selected]="draft().floorItem === null">Default floor</option>
                  @for (item of floorFinishes(); track item.itemId) {
                    <option [value]="item.itemId" [selected]="draft().floorItem?.itemId === item.itemId">{{ item.name }}</option>
                  }
                </select>
              </label>
            </section>

            <section class="card" aria-labelledby="placed-title">
              <h2 id="placed-title">In your room ({{ draft().items.length }} of {{ maxItems }})</h2>
              @if (draft().items.length === 0) {
                <p class="muted">Nothing yet. Place an item from the list below.</p>
              }
              <ul class="list">
                @for (p of draft().items; track p.itemId) {
                  <li>
                    <button type="button" class="pick" [attr.aria-pressed]="p.itemId === selectedId()" (click)="select(p.itemId)">
                      <strong>{{ nameOf(p.sku) }}</strong><span class="muted">: {{ where(p) }}</span>
                    </button>
                  </li>
                }
              </ul>
            </section>

            <section class="card" aria-labelledby="tray-title">
              <h2 id="tray-title">Your room items</h2>
              @if (placeable().length === 0 && unplaced().length === 0) {
                <p class="muted">You own no furniture or decor yet. <a routerLink="/shop" [queryParams]="{ filter: 'room' }">Find some in the shop</a>.</p>
              } @else if (unplaced().length === 0) {
                <p class="muted">All your items are in the room.</p>
              }
              @if (picturesFailed()) {
                <p class="notice small" role="status">Some item pictures could not load. Placeholders show instead.</p>
              }
              <ul class="tray">
                @for (item of unplaced(); track item.itemId) {
                  <li class="tray-item">
                    <vybe-item-thumb [src]="picture(item)" [name]="item.name" (failed)="picturesFailed.set(true)" />
                    <span>{{ item.name }}</span>
                    <button type="button" class="button secondary" (click)="place(item)">Place<span class="visually-hidden"> {{ item.name }}</span></button>
                  </li>
                }
              </ul>
            </section>
          </div>
        </div>
      }
    </section>
  `,
  styles: `
    .layout { display: grid; gap: 16px; }
    .side { display: grid; gap: 16px; align-content: start; }
    .stage { display: grid; gap: 8px; }
    .row { display: flex; gap: 8px; flex-wrap: wrap; }
    .small { font-size: 13px; margin: 0; }
    .controls h2, .card h2 { margin: 0 0 8px; font-size: 18px; }
    .controls h2:focus { outline: none; }
    .pad { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; margin: 8px 0; }
    .pad-button { min-height: 44px; border-radius: 10px; border: 1px solid #2b3350; background: #1b2135; color: inherit; font: inherit; cursor: pointer; }
    .pad-button:focus-visible, .pick:focus-visible { outline: 3px solid #ffc83d; outline-offset: 2px; }
    .field { display: grid; gap: 4px; margin-bottom: 8px; }
    select { min-height: 44px; padding: 6px 10px; border-radius: 10px; background: #1b2135; color: inherit; border: 1px solid #2b3350; font: inherit; }
    .list, .tray { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
    .pick { width: 100%; min-height: 44px; text-align: left; padding: 8px 10px; border-radius: 10px; border: 1px solid #2b3350; background: #1b2135; color: inherit; font: inherit; cursor: pointer; }
    .pick[aria-pressed="true"] { border-color: #2ee5ff; box-shadow: inset 0 0 0 1px #2ee5ff; }
    .tray-item { display: grid; grid-template-columns: 48px 1fr auto; gap: 10px; align-items: center; }
    .tray-item vybe-item-thumb { width: 48px; }
    .sr-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
    @media (min-width: 900px) {
      .layout { grid-template-columns: minmax(0, 3fr) minmax(280px, 2fr); align-items: start; }
      /* The room stays in view while the player works in the long list beside it. */
      .stage { position: sticky; top: 16px; }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Room {
  private readonly api = inject(API);
  private readonly injector = inject(Injector);
  private readonly stage = viewChild<ElementRef<HTMLElement>>("stage");
  private readonly selectedTitle = viewChild<ElementRef<HTMLElement>>("selectedTitle");
  private readonly reducedMotion = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  protected readonly maxItems = ROOM.maxItems;

  protected readonly loaded = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly inventory = signal<InventoryItem[]>([]);
  /** The stored room and its revision; the draft is what the player edits. */
  private readonly saved = signal<RoomState>(EMPTY_ROOM);
  private readonly revision = signal(0);
  protected readonly draft = signal<RoomState>(EMPTY_ROOM);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly conflict = signal(false);
  protected readonly message = signal<Message | null>(null);
  protected readonly announcement = signal("");
  protected readonly picturesFailed = signal(false);

  protected readonly dirty = computed(() => !sameRoom(this.saved(), this.draft()));
  protected readonly selected = computed(() => this.draft().items.find((p) => p.itemId === this.selectedId()) ?? null);
  protected readonly placeable = computed(() => this.inventory().filter((i) => i.slot === "furniture" || i.slot === "wall_decor"));
  protected readonly unplaced = computed(() => {
    const placed = new Set(this.draft().items.map((p) => p.itemId));
    return this.placeable().filter((i) => !placed.has(i.itemId));
  });
  protected readonly wallFinishes = computed(() => this.inventory().filter((i) => i.slot === "room_wall"));
  protected readonly floorFinishes = computed(() => this.inventory().filter((i) => i.slot === "room_floor"));
  protected readonly moves = computed((): Move[] => {
    const p = this.selected();
    if (p === null) return [];
    if (p.surface === "floor") {
      return [
        { dx: 0, dy: -1, label: "Move back", key: "back" },
        { dx: 0, dy: 1, label: "Move forward", key: "forward" },
        { dx: -1, dy: 0, label: "Move left", key: "left" },
        { dx: 1, dy: 0, label: "Move right", key: "right" },
      ];
    }
    const along = p.surface === "wall_back" ? ["Move left", "Move right"] : ["Move back", "Move forward"];
    return [
      { dx: 0, dy: 1, label: "Move up", key: "up" },
      { dx: 0, dy: -1, label: "Move down", key: "down" },
      { dx: -1, dy: 0, label: along[0]!, key: "previous" },
      { dx: 1, dy: 0, label: along[1]!, key: "next" },
    ];
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    this.conflict.set(false);
    try {
      const [room, inventory] = await Promise.all([this.api.getMyRoom(), this.api.getInventory()]);
      this.inventory.set(inventory.items);
      this.saved.set(fromApi(room));
      this.draft.set(fromApi(room));
      this.revision.set(room.revision);
      this.selectedId.set(null);
      this.message.set(null);
      this.loaded.set(true);
    } catch (error: unknown) {
      this.loadError.set(errorMessage(error));
    }
  }

  protected nameOf(sku: string): string {
    return roomItem(sku)?.name ?? sku;
  }

  protected where(p: Placement): string {
    return describePlacement(p);
  }

  protected picture(item: InventoryItem): string {
    return itemPicture(item.sku, item.previewUrl);
  }

  protected select(itemId: string | null): void {
    this.selectedId.set(this.selectedId() === itemId ? null : itemId);
    const p = this.selected();
    if (p !== null) this.announcement.set(`${this.nameOf(p.sku)} selected: ${describePlacement(p)}.`);
  }

  protected place(item: InventoryItem): void {
    if (this.draft().items.length >= ROOM.maxItems) {
      this.say(PROBLEMS.room_item_limit, true);
      return;
    }
    const spot = firstFree(this.draft().items, { itemId: item.itemId, sku: item.sku });
    if (spot === null) {
      this.say(`There is no free place for ${item.name}. Move or remove an item first.`, true);
      return;
    }
    this.apply(spot, `${item.name} placed: ${describePlacement(spot)}.`);
    this.selectedId.set(item.itemId);
    // The Place button is gone: focus goes to the item's controls, and the room scrolls into view (phones).
    afterNextRender(
      () => {
        this.selectedTitle()?.nativeElement.focus({ preventScroll: true });
        this.stage()?.nativeElement.scrollIntoView({ block: "nearest", behavior: this.reducedMotion ? "auto" : "smooth" });
      },
      { injector: this.injector },
    );
  }

  protected move(dx: number, dy: number): void {
    const p = this.selected();
    if (p !== null) this.tryPlace(moved(p, dx, dy));
  }

  protected rotate(): void {
    const p = this.selected();
    if (p !== null && p.surface === "floor") this.tryPlace(rotated(p));
  }

  protected switchWall(): void {
    const p = this.selected();
    if (p !== null && isWall(p.surface)) this.tryPlace(otherWall(p));
  }

  /** A tap in the room: the selected item goes to that cell, or the item there becomes selected. */
  protected moveTo(pick: Pick): void {
    const p = this.selected();
    const asset = p === null ? undefined : roomItem(p.sku);
    if (p !== null && asset?.surfaces.includes(pick.surface)) {
      this.tryPlace({ ...p, surface: pick.surface, x: pick.x, y: pick.y, rotation: isWall(pick.surface) ? 0 : p.rotation });
      return;
    }
    const hit = this.draft().items.find((q) => q.surface === pick.surface && q.x === pick.x && q.y === pick.y);
    if (hit !== undefined) this.select(hit.itemId);
    else if (p !== null) this.say(`${this.nameOf(p.sku)} cannot go on the ${pick.surface === "floor" ? "floor" : "wall"}.`, true);
  }

  protected remove(): void {
    const p = this.selected();
    if (p === null) return;
    this.draft.update((room) => ({ ...room, items: room.items.filter((q) => q.itemId !== p.itemId) }));
    this.selectedId.set(null);
    this.say(`${this.nameOf(p.sku)} removed from the room.`, false);
  }

  protected setFinish(kind: "wall" | "floor", event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    const item = this.inventory().find((i) => i.itemId === id);
    const ref: ItemRef | null = item === undefined ? null : { itemId: item.itemId, sku: item.sku };
    this.draft.update((room) => (kind === "wall" ? { ...room, wallItem: ref } : { ...room, floorItem: ref }));
    this.say(`${kind === "wall" ? "Walls" : "Floor"}: ${item?.name ?? "default"}.`, false);
  }

  protected discard(): void {
    this.draft.set(this.saved());
    this.selectedId.set(null);
    this.say("Changes discarded.", false);
  }

  protected async save(): Promise<void> {
    this.saving.set(true);
    this.message.set(null);
    this.conflict.set(false);
    try {
      const room = await this.api.saveMyRoom(toRequest(this.draft(), this.revision()));
      this.saved.set(fromApi(room));
      this.draft.set(fromApi(room));
      this.revision.set(room.revision);
      this.say("Your room is saved.", false);
    } catch (error: unknown) {
      if (isApiError(error, "revision_conflict")) {
        this.conflict.set(true);
        this.say("Your room changed in another tab or device. Load the saved room, then make your changes again.", true);
      } else {
        const itemId = isApiError(error) ? (error.problem as { itemId?: string } | null)?.itemId : undefined;
        if (itemId !== undefined && this.draft().items.some((p) => p.itemId === itemId)) this.selectedId.set(itemId);
        this.say(errorMessage(error), true);
      }
    }
    this.saving.set(false);
  }

  /** Arrow keys, R, Delete and Escape act on the selected item (not while a field has focus). */
  protected key(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (this.selected() === null || target.closest("select, input, textarea") !== null) return;
    const p = this.selected()!;
    const wall = isWall(p.surface);
    const keys: Record<string, () => void> = {
      ArrowLeft: () => this.move(-1, 0),
      ArrowRight: () => this.move(1, 0),
      ArrowUp: () => this.move(0, wall ? 1 : -1),
      ArrowDown: () => this.move(0, wall ? -1 : 1),
      r: () => (wall ? this.switchWall() : this.rotate()),
      R: () => (wall ? this.switchWall() : this.rotate()),
      Delete: () => this.remove(),
      Backspace: () => this.remove(),
      Escape: () => this.selectedId.set(null),
    };
    const action = keys[event.key];
    if (action === undefined) return;
    event.preventDefault();
    action();
  }

  private tryPlace(next: Placement): void {
    const result = withPlacement(this.draft().items, next);
    if (typeof result === "string") {
      this.say(PROBLEMS[result], true);
      return;
    }
    this.apply(next, `${this.nameOf(next.sku)}: ${describePlacement(next)}.`, result);
  }

  private apply(next: Placement, text: string, items?: Placement[]): void {
    const result = items ?? withPlacement(this.draft().items, next);
    if (typeof result === "string") return;
    this.draft.update((room) => ({ ...room, items: result }));
    this.message.set(null);
    this.announcement.set(text);
  }

  /** A visible message (role status or alert announces it); the hidden live region is for the other changes. */
  private say(text: string, error: boolean): void {
    this.message.set({ text, error });
    this.announcement.set("");
  }
}
