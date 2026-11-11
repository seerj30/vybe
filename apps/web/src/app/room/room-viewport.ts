import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { cells, isWall, type Placement, type RoomState } from "./room-layout";
import { ROOM, roomItem, type Surface } from "./room-manifest";
import type { Pick, RoomRenderer, RoomStats } from "./room-renderer";

type Mode = "loading" | "3d" | "fallback";

export const ROOM_WARNINGS = {
  items: "Some room items could not load, so boxes show instead. Your room did not change.",
  shell: "The 3D room could not load, so a plan shows instead.",
  webgl: "3D is not available on this device, so a plan shows instead.",
  lost: "The 3D view stopped. A plan shows until it restarts.",
} as const;

interface PlanTile {
  itemId: string;
  label: string;
  initials: string;
  column: string;
  row: string;
  selected: boolean;
  rug: boolean;
}

const SURFACES: readonly { surface: Surface; label: string }[] = [
  { surface: "wall_back", label: "Back wall" },
  { surface: "wall_left", label: "Left wall" },
  { surface: "floor", label: "Floor" },
];

function initials(name: string): string {
  return name
    .split(/\s+/)
    .map((word) => word.charAt(0))
    .join("")
    .slice(0, 2)
    .toUpperCase();
}

/**
 * My Room viewport (docs/design/p1-my-room.md §4): a lazy Three.js scene with an explicit mount and dispose, and a
 * 2D plan when 3D is not possible. It only shows the room; a tap on a cell emits `picked` for the editor. The
 * canvas is decorative: the editor's list and buttons are the accessible way to use the room.
 */
@Component({
  selector: "vybe-room-viewport",
  host: {
    "[attr.data-state]": "mode()",
    "[attr.data-items]": "shownItems()",
    "[attr.data-triangles]": "stats()?.triangles",
    "[attr.data-draw-calls]": "stats()?.drawCalls",
  },
  template: `
    <div class="stage" role="img" [attr.aria-label]="label()" [hidden]="mode() === 'fallback'" [attr.aria-busy]="mode() === 'loading'">
      @for (key of [canvasKey()]; track key) {
        <canvas #canvas aria-hidden="true" [class.editable]="editable()"></canvas>
      }
      @if (mode() === "loading") {
        <span class="loading" aria-hidden="true"></span>
      }
    </div>
    @if (mode() === "fallback") {
      <div class="plan" role="img" [attr.aria-label]="label()">
        @for (part of plan(); track part.surface) {
          <div class="surface">
            <span class="surface-label" aria-hidden="true">{{ part.label }}</span>
            <div
              class="grid"
              aria-hidden="true"
              [class.wall]="part.wall"
              [style.grid-template-columns]="'repeat(' + part.columns + ', 1fr)'"
              [style.grid-template-rows]="'repeat(' + part.rows + ', 1fr)'"
              (click)="pickPlan(part.surface, part.columns, part.rows, $event)"
            >
              @for (tile of part.tiles; track tile.itemId) {
                <span
                  class="tile"
                  [class.rug]="tile.rug"
                  [class.selected]="tile.selected"
                  [style.grid-column]="tile.column"
                  [style.grid-row]="tile.row"
                  [title]="tile.label"
                  >{{ tile.initials }}</span
                >
              }
            </div>
          </div>
        }
      </div>
    }
    @if (lostWithoutRestore()) {
      <button type="button" class="button secondary" (click)="restart()">Restart 3D</button>
    }
    @if (warning(); as text) {
      <p class="notice" role="status">{{ text }}</p>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 8px; width: 100%; }
    .stage { position: relative; width: 100%; aspect-ratio: 4 / 3; }
    .stage[hidden] { display: none; }
    canvas { display: block; width: 100%; height: 100%; touch-action: pan-y; }
    canvas.editable { cursor: pointer; }
    .loading { position: absolute; inset: 0; margin: auto; width: 36px; height: 36px; border-radius: 50%; border: 4px solid #2b3350; border-top-color: #ffc83d; animation: spin 0.9s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .loading { animation: none; } }
    .plan { display: grid; gap: 10px; }
    .surface { display: grid; gap: 4px; }
    .surface-label { font-size: 13px; color: #8a90a6; }
    .grid { display: grid; aspect-ratio: 1; gap: 2px; padding: 2px; border-radius: 8px; background: #2b3350; cursor: pointer; }
    .grid.wall { aspect-ratio: 8 / 5; background: #39405e; }
    .tile { display: grid; place-items: center; min-width: 0; border-radius: 6px; background: #5b6487; color: #fff; font-size: 12px; font-weight: 800; overflow: hidden; }
    .tile.rug { background: #46506f; color: #c9cfdb; outline: 2px dashed #8a90a6; outline-offset: -4px; }
    .tile.selected { outline: 3px solid #2ee5ff; outline-offset: -3px; }
    .notice { margin: 0; font-size: 13px; }
    @media (min-width: 768px) { .plan { grid-template-columns: 1fr 1fr; } .surface:last-child { grid-column: 1 / -1; max-width: 420px; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoomViewport {
  readonly room = input.required<RoomState>();
  readonly selected = input<string | null>(null);
  readonly editable = input(false);
  /** The owner's name for the label ("Your room" when null). */
  readonly owner = input<string | null>(null);
  readonly picked = output<Pick>();

  private readonly canvas = viewChild<ElementRef<HTMLCanvasElement>>("canvas");
  protected readonly mode = signal<Mode>("loading");
  protected readonly stats = signal<RoomStats | null>(null, {
    equal: (a, b) => a?.triangles === b?.triangles && a?.drawCalls === b?.drawCalls,
  });
  protected readonly lostWithoutRestore = signal(false);
  protected readonly canvasKey = signal(0);
  private readonly problem = signal<keyof typeof ROOM_WARNINGS | null>(null);
  private readonly failed = signal<readonly string[]>([]);
  private readonly shown = signal<readonly string[]>([]);
  private readonly injector = inject(Injector);
  private renderer: RoomRenderer | null = null;
  private lastRoom: RoomState | null = null;
  private lostTimer: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  protected readonly shownItems = computed(() => this.shown().join(" "));
  protected readonly warning = computed(() => {
    const problem = this.problem();
    if (problem !== null) return ROOM_WARNINGS[problem];
    return this.failed().length > 0 ? ROOM_WARNINGS.items : null;
  });
  protected readonly label = computed(() => {
    const room = this.room();
    const names = room.items.map((p) => roomItem(p.sku)?.name ?? p.sku);
    const finishes = [room.wallItem, room.floorItem].flatMap((ref) => (ref === null ? [] : [roomItem(ref.sku)?.name ?? ref.sku]));
    const whose = this.owner() === null ? "Your room" : `Room of ${this.owner()}`;
    const parts = [...finishes, ...names];
    return parts.length === 0 ? `${whose}: empty` : `${whose}: ${parts.join(", ")}`;
  });
  protected readonly plan = computed(() =>
    SURFACES.map(({ surface, label }) => {
      const g = isWall(surface) ? ROOM.wall : ROOM.floor;
      const tiles = this.room()
        .items.filter((p) => p.surface === surface)
        .flatMap((p) => this.tile(p, g.rows));
      // Rugs first, so objects draw on top of them.
      tiles.sort((a, b) => Number(b.rug) - Number(a.rug));
      return { surface, label, wall: isWall(surface), columns: g.columns, rows: g.rows, tiles };
    }),
  );

  constructor() {
    afterNextRender(() => void this.mount());
    effect(() => {
      const room = this.room();
      const selected = this.selected();
      untracked(() => {
        if (this.renderer !== null && this.mode() !== "fallback") {
          // Only the selection changed: move the frame, keep the scene.
          if (room === this.lastRoom && this.mode() === "3d") this.renderer.select(selected);
          else void this.show(room, selected);
        }
        if (this.mode() === "fallback") this.shown.set(room.items.map((p) => p.sku));
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      clearTimeout(this.lostTimer);
      this.unmount();
    });
  }

  /** The plan: a click on a grid picks the cell under the pointer (walls: row 0 is at the bottom). */
  protected pickPlan(surface: Surface, columns: number, rows: number, event: MouseEvent): void {
    if (!this.editable()) return;
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const x = Math.min(columns - 1, Math.max(0, Math.floor(((event.clientX - rect.left) / rect.width) * columns)));
    const fromTop = Math.min(rows - 1, Math.max(0, Math.floor(((event.clientY - rect.top) / rect.height) * rows)));
    this.picked.emit({ surface, x, y: isWall(surface) ? rows - 1 - fromTop : fromTop });
  }

  protected restart(): void {
    this.lostWithoutRestore.set(false);
    this.unmount();
    this.problem.set(null);
    this.failed.set([]);
    this.canvasKey.update((key) => key + 1);
    this.mode.set("loading");
    afterNextRender(() => void this.mount(), { injector: this.injector });
  }

  private tile(p: Placement, wallRows: number): PlanTile[] {
    const asset = roomItem(p.sku);
    if (asset === undefined) return [];
    const [w, d] = cells(asset, p.surface, p.rotation);
    // On a wall, row 0 is at the floor, so the grid counts from the bottom.
    const row = isWall(p.surface) ? `${wallRows - p.y - d + 1} / span ${d}` : `${p.y + 1} / span ${d}`;
    return [{
      itemId: p.itemId,
      label: asset.name,
      initials: initials(asset.name),
      column: `${p.x + 1} / span ${w}`,
      row,
      selected: p.itemId === this.selected(),
      rug: asset.layer === "rug",
    }];
  }

  private async mount(): Promise<void> {
    const canvas = this.canvas()?.nativeElement;
    if (canvas === undefined) return;
    this.mode.set("loading");
    try {
      const { RoomRenderer } = await import("./room-renderer");
      if (this.destroyed) return;
      this.renderer = new RoomRenderer(canvas, {
        onStats: (stats) => this.stats.set(stats),
        onPick: (pick) => {
          if (this.editable()) this.picked.emit(pick);
        },
        onContextLost: () => this.contextLost(),
        onContextRestored: () => this.contextRestored(),
      });
    } catch {
      this.renderer = null;
      this.fallback("webgl");
      return;
    }
    await this.show(this.room(), this.selected());
  }

  private async show(room: RoomState, selected: string | null): Promise<void> {
    const renderer = this.renderer;
    if (renderer === null) return;
    this.lastRoom = room;
    let report;
    try {
      report = await renderer.show(room, selected);
    } catch {
      if (renderer === this.renderer && !this.destroyed) this.fallback("shell");
      return;
    }
    if (!report.current || this.destroyed || renderer !== this.renderer) return;
    if (report.shellFailed) {
      this.fallback("shell");
      return;
    }
    this.failed.set(report.failed);
    this.shown.set(room.items.map((p) => p.sku).filter((sku) => !report.failed.includes(sku)));
    if (this.problem() !== "lost") this.mode.set("3d");
  }

  private fallback(problem: keyof typeof ROOM_WARNINGS): void {
    this.problem.set(problem);
    this.mode.set("fallback");
    this.shown.set(this.room().items.map((p) => p.sku));
    if (problem !== "lost") this.unmount();
  }

  private contextLost(): void {
    this.problem.set("lost");
    this.mode.set("fallback");
    clearTimeout(this.lostTimer);
    this.lostTimer = setTimeout(() => this.lostWithoutRestore.set(true), 5_000);
  }

  private contextRestored(): void {
    clearTimeout(this.lostTimer);
    this.lostWithoutRestore.set(false);
    this.problem.set(null);
    this.mode.set("3d");
  }

  private unmount(): void {
    this.renderer?.dispose();
    this.renderer = null;
  }
}
