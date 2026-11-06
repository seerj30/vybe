import { DecimalPipe } from "@angular/common";
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  type ElementRef,
  inject,
  isDevMode,
  signal,
  viewChild,
} from "@angular/core";
import {
  generateWaypoints,
  targetAt,
  type ShotEvent,
  type ShotResult,
  type StreetHoopsConfig,
  type Waypoint,
} from "@vybe/game-rules";
import { ballEndPoint, courtPoint, type CourtPoint } from "./court";
import { HoopsScene } from "./hoops-scene";
import { aimFromFraction, chargePower, stepAim } from "./input";
import { applyOverrides, loadRuleset, TUNABLE_KEYS, type Overrides, type RulesetFile, type TunableKey } from "./ruleset";
import {
  addRun,
  EMPTY_STATS,
  evaluateTrace,
  rates,
  shotGate,
  type LocalRun,
  type SessionStats,
  type ShotGate,
  type VerifiedResult,
} from "./trace";

type Phase = "loading" | "ready" | "running" | "finished" | "error";

/** Client feel values, not ruleset values: they never change the score. */
const KEYBOARD_AIM_UNITS_PER_S = 1500;
const FLIGHT_MS = 700;
const DEFAULT_CHARGE_CYCLE_MS = 1400;
const IDLE_HOOP = courtPoint(0, 600);
const UINT32_MAX = 0xffffffff;

const LEFT_KEYS = new Set(["ArrowLeft", "KeyA"]);
const RIGHT_KEYS = new Set(["ArrowRight", "KeyD"]);
const SHOOT_KEYS = new Set(["Space", "Enter"]);

const GATE_MESSAGES: Record<Exclude<ShotGate, "ok">, string> = {
  cooldown: "Too soon: wait for the meter",
  max_events: "Shot limit reached",
  time_up: "Time is up",
};

const RESULT_LABELS: Record<ShotResult["result"], string> = { perfect: "Perfect", hit: "Hit", miss: "Miss" };

interface ActiveRun extends LocalRun {
  waypoints: Waypoint[];
  startMs: number;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function randomSeed(): number {
  return crypto.getRandomValues(new Uint32Array(1))[0] ?? 0;
}

@Component({
  selector: "vybe-street-hoops",
  imports: [DecimalPipe],
  templateUrl: "./street-hoops.html",
  styleUrl: "./street-hoops.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    "(window:keydown)": "onKeyDown($event)",
    "(window:keyup)": "onKeyUp($event)",
    "(window:blur)": "cancelInput()",
  },
})
export class StreetHoops {
  protected readonly devMode = isDevMode();
  protected readonly tunableKeys = TUNABLE_KEYS;
  protected readonly labels = RESULT_LABELS;

  protected readonly phase = signal<Phase>("loading");
  protected readonly error = signal<string | null>(null);
  protected readonly ruleset = signal<RulesetFile | null>(null);
  protected readonly secondsLeft = signal(0);
  protected readonly score = signal(0);
  protected readonly streak = signal(0);
  protected readonly lastShot = signal<ShotResult | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly power = signal(0);
  protected readonly coolingDown = signal(false);
  protected readonly summary = signal<VerifiedResult | null>(null);
  protected readonly reducedMotion = signal(matchMedia("(prefers-reduced-motion: reduce)").matches);

  // Dev tuning panel.
  protected readonly panelOpen = matchMedia("(min-width: 768px)").matches;
  protected readonly overrides = signal<Overrides>({});
  protected readonly chargeCycleMs = signal(DEFAULT_CHARGE_CYCLE_MS);
  protected readonly showReticle = signal(true);
  protected readonly seedInput = signal("");
  protected readonly lastSeed = signal<number | null>(null);
  protected readonly stats = signal<SessionStats>(EMPTY_STATS);
  protected readonly statRates = computed(() => rates(this.stats()));
  protected readonly fps = signal(0);

  protected readonly effective = computed((): { config: StreetHoopsConfig | null; problem: string | null } => {
    const ruleset = this.ruleset();
    if (ruleset === null) return { config: null, problem: null };
    try {
      return { config: applyOverrides(ruleset.config, this.overrides()), problem: null };
    } catch (error: unknown) {
      return { config: null, problem: errorMessage(error) };
    }
  });
  protected readonly effectiveJson = computed(() => JSON.stringify(this.effective().config, null, 2));
  protected readonly seedProblem = computed(() => {
    const text = this.seedInput().trim();
    if (text === "") return null;
    const seed = Number(text);
    return Number.isInteger(seed) && seed >= 0 && seed <= UINT32_MAX ? null : "Seed must be an integer in [0, 4294967295]";
  });
  protected readonly lastShotLabel = computed(() => {
    const shot = this.lastShot();
    if (shot === null) return "";
    const points = shot.points > 0 ? ` +${shot.points}` : "";
    return `${RESULT_LABELS[shot.result]}${points} · streak ${shot.streak}`;
  });

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>("court");
  private readonly stage = viewChild.required<ElementRef<HTMLElement>>("stage");

  private scene: HoopsScene | null = null;
  private run: ActiveRun | null = null;
  private events: ShotEvent[] = [];
  private aimX = 0;
  private chargeStartMs: number | null = null;
  private readonly heldKeys = new Set<string>();
  private hoop: CourtPoint = IDLE_HOOP;
  private statsKey = "";
  private rafId = 0;
  private lastFrameMs = 0;
  private fpsWindowStart = 0;
  private fpsFrames = 0;

  constructor() {
    const destroyRef = inject(DestroyRef);

    loadRuleset().then(
      (ruleset) => {
        this.ruleset.set(ruleset);
        if (this.phase() === "loading") this.phase.set("ready");
      },
      (error: unknown) => this.fail(errorMessage(error)),
    );

    afterNextRender(() => {
      const canvas = this.canvas().nativeElement;
      try {
        this.scene = new HoopsScene(canvas);
      } catch (error: unknown) {
        this.fail(`WebGL is not available: ${errorMessage(error)}`);
        return;
      }
      canvas.addEventListener("webglcontextlost", (event) => {
        event.preventDefault();
        this.fail("The graphics context was lost. Reload the page to play again.");
      });
      const observer = new ResizeObserver(() => this.resize());
      observer.observe(this.stage().nativeElement);
      this.resize();
      this.rafId = requestAnimationFrame(this.frame);
      destroyRef.onDestroy(() => {
        cancelAnimationFrame(this.rafId);
        observer.disconnect();
        this.scene?.dispose();
        this.scene = null;
      });
    });
  }

  protected start(): void {
    const ruleset = this.ruleset();
    const { config } = this.effective();
    if (ruleset === null || config === null || this.seedProblem() !== null || this.scene === null) return;

    const seedText = this.seedInput().trim();
    const seed = seedText === "" ? randomSeed() : Number(seedText);
    const key = JSON.stringify(config);
    if (key !== this.statsKey) {
      this.statsKey = key;
      this.stats.set(EMPTY_STATS);
    }

    this.run = { config, rulesetId: ruleset.id, seed, waypoints: generateWaypoints(config, seed), startMs: performance.now() };
    this.events = [];
    this.aimX = 0;
    this.chargeStartMs = null;
    this.scene.setThresholds(config);
    this.scene.clearBalls();
    this.score.set(0);
    this.streak.set(0);
    this.lastShot.set(null);
    this.notice.set(null);
    this.summary.set(null);
    this.lastSeed.set(seed);
    this.secondsLeft.set(Math.ceil(config.durationMs / 1000));
    this.phase.set("running");
    this.canvas().nativeElement.focus();
  }

  protected onPointerDown(event: PointerEvent): void {
    if (this.phase() !== "running" || !event.isPrimary) return;
    this.aimAtPointer(event);
    this.startCharge(event.timeStamp);
    // Keeps the release on the canvas when the finger leaves it. Synthetic test events have no active pointer.
    if (event.isTrusted) this.canvas().nativeElement.setPointerCapture(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.phase() === "running" && event.isPrimary) this.aimAtPointer(event);
  }

  protected onPointerUp(event: PointerEvent): void {
    if (event.isPrimary) this.release(event.timeStamp);
  }

  protected onKeyDown(event: KeyboardEvent): void {
    if (this.phase() !== "running" || this.isFormField(event.target)) return;
    if (LEFT_KEYS.has(event.code) || RIGHT_KEYS.has(event.code)) {
      this.heldKeys.add(event.code);
      event.preventDefault();
    } else if (SHOOT_KEYS.has(event.code)) {
      event.preventDefault();
      if (!event.repeat) this.startCharge(event.timeStamp);
    }
  }

  protected onKeyUp(event: KeyboardEvent): void {
    this.heldKeys.delete(event.code);
    if (SHOOT_KEYS.has(event.code) && this.phase() === "running") {
      // A checkbox toggles on the Space keyup, so keep the default on form fields.
      if (!this.isFormField(event.target)) event.preventDefault();
      this.release(event.timeStamp);
    }
  }

  /** Drops a held shot without recording it (focus loss, pointer cancel). */
  protected cancelInput(): void {
    this.heldKeys.clear();
    this.chargeStartMs = null;
    this.power.set(0);
  }

  protected setOverride(key: TunableKey, event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    const { [key]: _removed, ...rest } = this.overrides();
    this.overrides.set(text === "" ? rest : { ...rest, [key]: Number(text) });
  }

  protected setChargeCycle(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    if (Number.isFinite(value) && value >= 200) this.chargeCycleMs.set(value);
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected inputChecked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected resetTuning(): void {
    this.overrides.set({});
    this.chargeCycleMs.set(DEFAULT_CHARGE_CYCLE_MS);
    this.seedInput.set("");
    this.showReticle.set(true);
  }

  private readonly frame = (now: number): void => {
    this.rafId = requestAnimationFrame(this.frame);
    const dtMs = this.lastFrameMs === 0 ? 0 : now - this.lastFrameMs;
    this.lastFrameMs = now;
    this.countFrame(now);

    let reticle: CourtPoint | null = null;
    const run = this.run;
    if (this.phase() === "running" && run !== null) {
      // The rAF timestamp is the frame start, so the first frame after start() can be earlier than startMs.
      const elapsed = Math.max(0, now - run.startMs);
      if (elapsed >= run.config.durationMs) {
        this.finish(run);
      } else {
        const tMs = Math.floor(elapsed);
        this.aimX = stepAim(this.aimX, this.aimDirection(), dtMs, KEYBOARD_AIM_UNITS_PER_S);
        const power = this.chargeStartMs === null ? 0 : chargePower(now - this.chargeStartMs, this.chargeCycleMs());
        this.power.set(power);
        const target = targetAt(run.config, run.waypoints, tMs);
        this.hoop = courtPoint(target.targetX, target.idealPower);
        if (this.showReticle()) reticle = courtPoint(this.aimX, power);
        this.secondsLeft.set(Math.ceil((run.config.durationMs - elapsed) / 1000));
        this.coolingDown.set(shotGate(run.config, this.events, tMs) === "cooldown");
      }
    }
    this.scene?.render({ nowMs: now, hoop: this.hoop, reticle });
  };

  private startCharge(nowMs: number): void {
    this.chargeStartMs = nowMs;
    this.notice.set(null);
  }

  /** One release = one shot event (spec §7), scored at once through the reference simulator. */
  private release(nowMs: number): void {
    const run = this.run;
    const chargeStartMs = this.chargeStartMs;
    if (run === null || chargeStartMs === null || this.phase() !== "running") return;
    this.chargeStartMs = null;
    this.power.set(0);

    const tMs = Math.floor(nowMs - run.startMs);
    const gate = shotGate(run.config, this.events, tMs);
    if (gate !== "ok") {
      this.notice.set(GATE_MESSAGES[gate]);
      return;
    }
    const shotEvent: ShotEvent = { tMs, aimX: this.aimX, power: chargePower(nowMs - chargeStartMs, this.chargeCycleMs()) };
    this.events = [...this.events, shotEvent];

    let result: VerifiedResult;
    try {
      result = evaluateTrace(run, this.events, tMs);
    } catch (error: unknown) {
      this.fail(errorMessage(error));
      return;
    }
    const shot = result.shots[result.shots.length - 1];
    if (shot === undefined) return;
    this.score.set(result.score);
    this.streak.set(shot.streak);
    this.lastShot.set(shot);

    // The ball meets the basket where it is when the ball arrives; the simulator result decides in or out.
    const flightMs = this.reducedMotion() ? 0 : FLIGHT_MS;
    const arrival = targetAt(run.config, run.waypoints, Math.min(tMs + flightMs, run.config.durationMs - 1));
    const hoop = courtPoint(arrival.targetX, arrival.idealPower);
    const end = ballEndPoint(shot.result, courtPoint(shotEvent.aimX, shotEvent.power), hoop);
    this.scene?.launch(shot.result, end, hoop, nowMs, flightMs, this.reducedMotion());
  }

  private finish(run: ActiveRun): void {
    this.cancelInput();
    try {
      const result = evaluateTrace(run, this.events, run.config.durationMs);
      this.summary.set(result);
      this.stats.update((stats) => addRun(stats, result.score, result.shots));
    } catch (error: unknown) {
      this.fail(errorMessage(error));
      return;
    }
    this.run = null;
    this.secondsLeft.set(0);
    this.coolingDown.set(false);
    this.phase.set("finished");
  }

  private fail(message: string): void {
    this.run = null;
    this.cancelInput();
    this.error.set(message);
    this.phase.set("error");
  }

  private aimAtPointer(event: PointerEvent): void {
    const rect = this.canvas().nativeElement.getBoundingClientRect();
    if (rect.width > 0) this.aimX = aimFromFraction((event.clientX - rect.left) / rect.width);
  }

  private aimDirection(): -1 | 0 | 1 {
    const left = [...LEFT_KEYS].some((k) => this.heldKeys.has(k));
    const right = [...RIGHT_KEYS].some((k) => this.heldKeys.has(k));
    return left === right ? 0 : left ? -1 : 1;
  }

  private isFormField(target: EventTarget | null): boolean {
    return target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement;
  }

  private resize(): void {
    const { clientWidth, clientHeight } = this.stage().nativeElement;
    if (clientWidth > 0 && clientHeight > 0) this.scene?.resize(clientWidth, clientHeight, devicePixelRatio);
  }

  private countFrame(now: number): void {
    this.fpsFrames++;
    if (now - this.fpsWindowStart >= 500) {
      this.fps.set(Math.round((this.fpsFrames * 1000) / (now - this.fpsWindowStart)));
      this.fpsWindowStart = now;
      this.fpsFrames = 0;
    }
  }
}
