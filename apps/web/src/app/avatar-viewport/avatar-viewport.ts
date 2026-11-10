import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  type ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { AVATAR_3D, itemAsset } from "../avatar/asset-manifest";
import { AvatarPortrait } from "../avatar/avatar-portrait";
import { EMPTY_OUTFIT, type Look, type Outfit, fallbackOutfit, resolveLook } from "../avatar/avatar-look";
import { type AvatarPresets, describeAvatar } from "../avatar/presets";
import type { AvatarRenderer, RenderStats } from "./avatar-renderer";

type Mode = "loading" | "3d" | "fallback";

const TURN_STEP = Math.PI / 6;
let nextId = 0;
const DRAG_RADIANS_PER_PX = 0.012;

export const WARNINGS = {
  items: "Some items could not load, so starter items show instead. What you wear did not change.",
  fit: "Some items do not fit this body, so starter items show instead.",
  base: "The 3D avatar could not load, so a 2D picture shows instead.",
  webgl: "3D is not available on this device, so a 2D picture shows instead.",
  lost: "The 3D view stopped. A 2D picture shows until it restarts.",
} as const;

/**
 * 3D avatar (spec §9, §10): an isolated Three.js viewport with an explicit mount and dispose. Three.js loads
 * lazily on first use. It only renders: an asset that fails to load falls back to the starter item (or the 2D
 * portrait) with a non-blocking notice, and never changes what the server says the player wears.
 */
@Component({
  selector: "vybe-avatar-viewport",
  imports: [AvatarPortrait],
  host: {
    "[attr.data-state]": "mode()",
    "[attr.data-items]": "shownItems()",
    "[attr.data-triangles]": "stats()?.triangles",
    "[attr.data-draw-calls]": "stats()?.drawCalls",
  },
  template: `
    @if (mode() === "fallback") {
      <vybe-avatar-portrait class="portrait" [presets]="presets()" />
    }
    <div
      class="stage"
      role="img"
      [attr.aria-label]="label()"
      [attr.aria-describedby]="wearing() ? descriptionId : null"
      [hidden]="mode() === 'fallback'"
      [attr.aria-busy]="mode() === 'loading'"
    >
      @for (key of [canvasKey()]; track key) {
        <canvas
          #canvas
          aria-hidden="true"
          (pointerdown)="dragStart($event)"
          (pointermove)="drag($event)"
          (pointerup)="dragEnd($event)"
          (pointercancel)="dragEnd($event)"
        ></canvas>
      }
      @if (mode() === "loading") {
        <span class="loading" aria-hidden="true"></span>
      }
    </div>
    <p class="visually-hidden" [id]="descriptionId">{{ wearing() }}</p>
    @if (mode() === "3d") {
      <div class="controls">
        <button type="button" class="turn" (click)="turn(-1)" aria-label="Turn the avatar left">⟲</button>
        <button type="button" class="turn" (click)="turn(1)" aria-label="Turn the avatar right">⟳</button>
        @if (emoteName(); as name) {
          <button type="button" class="button secondary" [disabled]="playing()" (click)="playEmote()">Play {{ name }}</button>
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
    :host { display: flex; flex-direction: column; align-items: center; gap: 8px; width: 100%; }
    .stage { position: relative; width: 100%; aspect-ratio: 3 / 4; }
    .stage[hidden] { display: none; }
    canvas { display: block; width: 100%; height: 100%; touch-action: pan-y; cursor: grab; }
    canvas:active { cursor: grabbing; }
    .portrait { width: 70%; max-width: 200px; }
    .loading { position: absolute; inset: 0; margin: auto; width: 36px; height: 36px; border-radius: 50%; border: 4px solid #2b3350; border-top-color: #ffc83d; animation: spin 0.9s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .loading { animation: none; } }
    .controls { display: flex; gap: 8px; flex-wrap: wrap; justify-content: center; }
    .turn { min-width: 44px; min-height: 44px; border-radius: 10px; border: 1px solid #2b3350; background: #1b2135; color: inherit; font-size: 20px; cursor: pointer; }
    .turn:focus-visible { outline: 3px solid #ffc83d; outline-offset: 2px; }
    .notice { margin: 0; font-size: 13px; text-align: center; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AvatarViewport {
  readonly presets = input.required<AvatarPresets>();
  readonly outfit = input<Outfit>(EMPTY_OUTFIT);
  /** An emote sku to preview: it plays once when it changes (not with reduced motion) and on its button. */
  readonly emote = input<string | null>(null);

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>("canvas");
  protected readonly mode = signal<Mode>("loading");
  // Equal numbers are not a change: the idle loop renders every frame.
  protected readonly stats = signal<RenderStats | null>(null, {
    equal: (a, b) => a?.triangles === b?.triangles && a?.drawCalls === b?.drawCalls,
  });
  protected readonly playing = signal(false);
  protected readonly lostWithoutRestore = signal(false);
  /** A lost WebGL context cannot be replaced on the same canvas: a restart renders a new one. */
  protected readonly canvasKey = signal(0);
  private readonly injector = inject(Injector);
  private readonly failed = signal<ReadonlySet<string>>(new Set());
  private readonly problem = signal<keyof typeof WARNINGS | null>(null);
  private readonly shown = signal<readonly string[]>([]);
  private readonly reducedMotion = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  private renderer: AvatarRenderer | null = null;
  private dragX: number | null = null;
  private lostTimer: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  protected readonly look = computed((): Look => resolveLook(this.presets(), fallbackOutfit(this.outfit(), this.failed())), {
    equal: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  });
  private autoPlayed: string | null = null;
  private emoteRun = 0;
  protected readonly shownItems = computed(() => this.shown().join(" "));
  protected readonly emoteName = computed(() => {
    const sku = this.emote();
    return sku === null ? null : (itemAsset(sku)?.name ?? null);
  });
  protected readonly warning = computed(() => {
    const problem = this.problem();
    if (problem !== null) return WARNINGS[problem];
    if (this.failed().size > 0) return WARNINGS.items;
    return this.look().skipped.length > 0 ? WARNINGS.fit : null;
  });
  /** The same name as the 2D portrait; the worn items are the description. */
  protected readonly label = computed(() => `Avatar: ${describeAvatar(this.presets())}`);
  protected readonly wearing = computed(() => {
    const names = this.look().parts.map((p) => itemAsset(p.sku)?.name ?? p.sku);
    return names.length > 0 ? `Wearing ${names.join(", ")}.` : "";
  });
  protected readonly descriptionId = `avatar-wearing-${nextId++}`;

  constructor() {
    afterNextRender(() => void this.mount());
    effect(() => {
      const look = this.look();
      untracked(() => {
        if (this.renderer !== null && this.mode() !== "fallback") void this.show(look);
      });
    });
    // A new emote preview plays once by itself (not with reduced motion); the button plays it again.
    effect(() => {
      const sku = this.emote();
      const ready = this.mode() === "3d";
      if (sku === null || !ready || this.reducedMotion || sku === this.autoPlayed) return;
      this.autoPlayed = sku;
      untracked(() => void this.playEmote());
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      clearTimeout(this.lostTimer);
      this.unmount();
    });
  }

  protected turn(direction: 1 | -1): void {
    this.renderer?.turn(direction * TURN_STEP);
  }

  protected dragStart(event: PointerEvent): void {
    if (event.pointerType === "mouse" && event.button !== 0) return;
    this.dragX = event.clientX;
    (event.target as Element).setPointerCapture?.(event.pointerId);
  }

  protected drag(event: PointerEvent): void {
    if (this.dragX === null) return;
    this.renderer?.turn((event.clientX - this.dragX) * DRAG_RADIANS_PER_PX);
    this.dragX = event.clientX;
  }

  protected dragEnd(event: PointerEvent): void {
    this.dragX = null;
    (event.target as Element).releasePointerCapture?.(event.pointerId);
  }

  protected async playEmote(): Promise<void> {
    const url = itemAsset(this.emote() ?? "")?.assetUrl;
    if (url === undefined || this.renderer === null) return;
    const run = ++this.emoteRun;
    this.playing.set(true);
    const result = await this.renderer.playEmote(url);
    // A newer emote (another preview) replaced this one: it owns the state now.
    if (run !== this.emoteRun || this.destroyed) return;
    this.playing.set(false);
    if (result === "failed") this.problem.set("items");
  }

  /** After a context loss without a restore: a new renderer and canvas state (spec §10 reloadable scene). */
  protected restart(): void {
    this.lostWithoutRestore.set(false);
    this.unmount();
    this.problem.set(null);
    this.failed.set(new Set());
    this.canvasKey.update((key) => key + 1);
    this.mode.set("loading");
    afterNextRender(() => void this.mount(), { injector: this.injector });
  }

  private async mount(): Promise<void> {
    this.mode.set("loading");
    try {
      const { AvatarRenderer } = await import("./avatar-renderer");
      if (this.destroyed) return;
      this.renderer = new AvatarRenderer(this.canvas().nativeElement, {
        reducedMotion: this.reducedMotion,
        idleAnimation: AVATAR_3D.idleAnimation,
        onStats: (stats) => this.stats.set(stats),
        onContextLost: () => this.contextLost(),
        onContextRestored: () => this.contextRestored(),
      });
    } catch {
      // No WebGL (or the 3D bundle did not load): the 2D portrait stays.
      this.renderer = null;
      this.problem.set("webgl");
      this.mode.set("fallback");
      return;
    }
    await this.show(this.look());
  }

  private async show(look: Look): Promise<void> {
    const renderer = this.renderer;
    if (renderer === null) return;
    let report;
    try {
      report = await renderer.show(look);
    } catch {
      // A scene error must not become an uncaught rejection: the 2D portrait is the safe state.
      if (renderer === this.renderer && !this.destroyed) {
        this.problem.set("base");
        this.mode.set("fallback");
        this.unmount();
      }
      return;
    }
    if (!report.current || this.destroyed || renderer !== this.renderer) return;
    if (report.baseFailed) {
      this.problem.set("base");
      this.mode.set("fallback");
      this.unmount();
      return;
    }
    if (report.failed.length > 0) {
      // The look recomputes with starter items; the effect shows it.
      this.failed.update((failed) => new Set([...failed, ...report.failed]));
      return;
    }
    this.shown.set(look.parts.map((p) => p.sku));
    if (this.problem() !== "lost") this.mode.set("3d");
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
    this.playing.set(false);
  }
}
