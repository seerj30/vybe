import { DecimalPipe } from "@angular/common";
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  type ElementRef,
  inject,
  input,
  isDevMode,
  type OnInit,
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
import { isApiError } from "../api/api-client";
import { RouterLink } from "@angular/router";
import { API } from "../api/api";
import { errorMessage as playerMessage } from "../api/messages";
import { PlayerStore } from "../player/player-store";
import { ballEndPoint, courtPoint, POWER_GUIDES, type CourtPoint } from "./court";
import { HoopsScene } from "./hoops-scene";
import { isRewardEligible, noCoinsText, type OnlineRun, OnlineSessions, rejectionText, type RunOutcome } from "./online-session";
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

type Phase = "loading" | "ready" | "preparing" | "running" | "submitting" | "finished" | "error";

/** `practice`: client-only prototype with the dev tuning panel. `online`: API session, server-verified rewards. */
export type Mode = "practice" | "online";

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

/** A scored shot whose result the HUD shows when the ball reaches the rim. */
interface PendingReveal {
  atMs: number;
  score: number;
  shot: ShotResult;
}

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
  imports: [DecimalPipe, RouterLink],
  templateUrl: "./street-hoops.html",
  styleUrl: "./street-hoops.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    "(window:keydown)": "onKeyDown($event)",
    "(window:keyup)": "onKeyUp($event)",
    "(window:blur)": "cancelInput()",
  },
})
export class StreetHoops implements OnInit {
  /** Bound from the route data. */
  readonly mode = input<Mode>("practice");
  /** Query parameters (online mode): `challenge` plays the recipient run; `rematchOf` and `to` go to the challenge made from the run. */
  readonly challenge = input<string | undefined>();
  readonly rematchOf = input<string | undefined>();
  readonly to = input<string | undefined>();

  protected readonly devMode = isDevMode();
  protected readonly tunableKeys = TUNABLE_KEYS;
  protected readonly labels = RESULT_LABELS;
  protected readonly powerGuides = POWER_GUIDES;

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

  // Online mode: the server outcome of the last run, or why it is not known yet.
  protected readonly outcome = signal<RunOutcome | null>(null);
  protected readonly onlineError = signal<string | null>(null);
  protected readonly canRetrySubmit = signal(false);
  protected readonly rejectionText = rejectionText;
  protected readonly noCoinsText = noCoinsText;
  /** Query parameters for `/challenges` when the last run can start a challenge (spec §2 J2), else null. */
  protected readonly challengeParams = computed((): Record<string, string> | null => {
    const outcome = this.outcome();
    const sessionId = this.verifiedSessionId();
    if (this.challenge() !== undefined || outcome?.status !== "verified" || sessionId === null) return null;
    if (!isRewardEligible(outcome.rewards)) return null;
    const params: Record<string, string> = { session: sessionId };
    const rematchOf = this.rematchOf();
    const to = this.to();
    if (rematchOf !== undefined) params["rematchOf"] = rematchOf;
    else if (to !== undefined) params["to"] = to;
    return params;
  });
  private readonly verifiedSessionId = signal<string | null>(null);

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
  protected readonly canStart = computed(
    () => this.mode() === "online" || (this.effective().config !== null && this.seedProblem() === null),
  );
  protected readonly lastShotLabel = computed(() => {
    const shot = this.lastShot();
    if (shot === null) return "";
    const points = shot.points > 0 ? ` +${shot.points}` : "";
    return `${RESULT_LABELS[shot.result]}${points} · streak ${shot.streak}`;
  });

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>("court");
  private readonly stage = viewChild.required<ElementRef<HTMLElement>>("stage");

  private scene: HoopsScene | null = null;
  private readonly sessions = new OnlineSessions(inject(API));
  private readonly player = inject(PlayerStore);
  private onlineRun: OnlineRun | null = null;
  private run: ActiveRun | null = null;
  private events: ShotEvent[] = [];
  private pendingReveals: PendingReveal[] = [];
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

  ngOnInit(): void {
    if (this.mode() === "online") {
      // Each online run gets its ruleset from its session.
      this.phase.set("ready");
      return;
    }
    loadRuleset().then(
      (ruleset) => {
        this.ruleset.set(ruleset);
        if (this.phase() === "loading") this.phase.set("ready");
      },
      (error: unknown) => this.fail(errorMessage(error)),
    );
  }

  protected start(): void {
    if (this.mode() === "online") {
      void this.startOnline();
      return;
    }
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

    this.beginRun(config, ruleset.id, seed);
  }

  /** Retries a submission whose outcome is unknown: reads the session first, then sends the same key. */
  protected async retrySubmit(): Promise<void> {
    const online = this.onlineRun;
    const summary = this.summary();
    if (online === null || summary === null) return;
    this.phase.set("submitting");
    this.onlineError.set(null);
    this.canRetrySubmit.set(false);
    try {
      const known = await this.sessions.reconcile(online.sessionId);
      this.settle(known ?? (await this.sessions.submit(online, this.events, summary.score)));
    } catch (error: unknown) {
      this.submitFailed(error);
    }
  }

  private async startOnline(): Promise<void> {
    if (this.scene === null) return;
    this.phase.set("preparing");
    this.outcome.set(null);
    this.onlineError.set(null);
    this.canRetrySubmit.set(false);
    this.summary.set(null);
    this.verifiedSessionId.set(null);
    let online: OnlineRun;
    try {
      online = await this.sessions.prepare(this.challenge());
    } catch (error: unknown) {
      this.onlineError.set(isApiError(error) ? playerMessage(error) : errorMessage(error));
      this.phase.set("ready");
      return;
    }
    // The server clock started when startSession answered, so local time 0 is now (spec §5).
    this.onlineRun = online;
    this.ruleset.set(online.ruleset);
    this.beginRun(online.ruleset.config, online.ruleset.id, online.seed);
  }

  private beginRun(config: StreetHoopsConfig, rulesetId: string, seed: number): void {
    if (this.scene === null) return;
    this.run = { config, rulesetId, seed, waypoints: generateWaypoints(config, seed), startMs: performance.now() };
    this.events = [];
    this.pendingReveals = [];
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
    this.startCharge(performance.now());
    // Keeps the release on the canvas when the finger leaves it. Synthetic test events have no active pointer.
    if (event.isTrusted) this.canvas().nativeElement.setPointerCapture(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.phase() === "running" && event.isPrimary) this.aimAtPointer(event);
  }

  protected onPointerUp(event: PointerEvent): void {
    if (event.isPrimary) this.release(performance.now());
  }

  protected onKeyDown(event: KeyboardEvent): void {
    if (this.phase() !== "running" || this.isFormField(event.target)) return;
    if (LEFT_KEYS.has(event.code) || RIGHT_KEYS.has(event.code)) {
      this.heldKeys.add(event.code);
      event.preventDefault();
    } else if (SHOOT_KEYS.has(event.code)) {
      event.preventDefault();
      if (!event.repeat) this.startCharge(performance.now());
    }
  }

  protected onKeyUp(event: KeyboardEvent): void {
    this.heldKeys.delete(event.code);
    if (SHOOT_KEYS.has(event.code) && this.phase() === "running") {
      // A checkbox toggles on the Space keyup, so keep the default on form fields.
      if (!this.isFormField(event.target)) event.preventDefault();
      this.release(performance.now());
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
    this.revealDue(now);

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

    // The ball meets the basket where it is when the ball arrives; the simulator result decides in or out.
    // The HUD shows the result at that moment, so the label never runs ahead of the ball.
    const flightMs = this.reducedMotion() ? 0 : FLIGHT_MS;
    this.pendingReveals = [...this.pendingReveals, { atMs: nowMs + flightMs, score: result.score, shot }];
    this.revealDue(nowMs);
    const arrival = targetAt(run.config, run.waypoints, Math.min(tMs + flightMs, run.config.durationMs - 1));
    const hoop = courtPoint(arrival.targetX, arrival.idealPower);
    const end = ballEndPoint(shot.result, courtPoint(shotEvent.aimX, shotEvent.power), hoop);
    this.scene?.launch(shot.result, end, hoop, nowMs, flightMs, this.reducedMotion());
  }

  private finish(run: ActiveRun): void {
    this.cancelInput();
    this.revealDue(Number.POSITIVE_INFINITY);
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
    if (this.mode() === "online") {
      void this.submitOnline();
    } else {
      this.phase.set("finished");
    }
  }

  private async submitOnline(): Promise<void> {
    const online = this.onlineRun;
    const summary = this.summary();
    if (online === null || summary === null) return;
    this.phase.set("submitting");
    try {
      this.settle(await this.sessions.submit(online, this.events, summary.score));
    } catch (error: unknown) {
      this.submitFailed(error);
    }
  }

  private settle(outcome: RunOutcome | null): void {
    if (outcome === null) {
      this.onlineError.set("VYBE is still checking your run.");
      this.canRetrySubmit.set(true);
    } else {
      this.outcome.set(outcome);
      this.verifiedSessionId.set(outcome.status === "verified" ? (this.onlineRun?.sessionId ?? null) : null);
      // Wallet, XP and level come from the server, never from local arithmetic (spec §10).
      void this.player.refresh().catch(() => undefined);
    }
    this.phase.set("finished");
  }

  private submitFailed(error: unknown): void {
    this.onlineError.set(playerMessage(error));
    this.canRetrySubmit.set(isApiError(error) && error.retryable);
    this.phase.set("finished");
  }

  /** Shows the latest result whose ball has reached the rim. */
  private revealDue(nowMs: number): void {
    const due = this.pendingReveals.filter((r) => r.atMs <= nowMs);
    const latest = due[due.length - 1];
    if (latest === undefined) return;
    this.pendingReveals = this.pendingReveals.filter((r) => r.atMs > nowMs);
    this.score.set(latest.score);
    this.streak.set(latest.shot.streak);
    this.lastShot.set(latest.shot);
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
