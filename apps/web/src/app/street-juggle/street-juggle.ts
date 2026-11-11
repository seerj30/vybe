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
import { RouterLink } from "@angular/router";
import { AchievementToast } from "../achievements/achievement-toast";
import {
  evaluateJuggle,
  type JuggleEvaluateResult,
  type JuggleTouch,
  type JuggleTouchResult,
  type StreetJuggleConfig,
  StreetJuggleSim,
} from "@vybe/game-rules";
import { API } from "../api/api";
import { isApiError } from "../api/api-client";
import { errorMessage as playerMessage } from "../api/messages";
import { FALLBACK_COLOR, hairColor, skinColor } from "../avatar/presets";
import { PlayerStore } from "../player/player-store";
import { isRewardEligible, type OnlineRun, OnlineSessions, type RunOutcome, type SessionGame } from "../street-hoops/online-session";
import fixture from "../../../../../packages/game-rules/fixtures/rulesets/street-juggle-v1.json";
import { laneForKey, touchGate, touchTime, type TouchGate } from "./juggle-input";
import { juggleNoCoinsText, juggleRejectionText } from "./juggle-text";
import {
  applyJuggleOverrides,
  JUGGLE_TUNABLE_KEYS,
  type JuggleOverrides,
  type JuggleRuleset,
  type JuggleTunableKey,
  loadJuggleRuleset,
  verifyJuggleRuleset,
} from "./juggle-ruleset";
import { type AvatarColors, JuggleScene } from "./juggle-scene";
import { ballLanePosition, type Flight, flightTicks, touchLabel } from "./juggle-view";

type Phase = "loading" | "ready" | "preparing" | "running" | "submitting" | "finished" | "error";
export type Mode = "practice" | "online";
type Verified = Extract<JuggleEvaluateResult, { outcome: "verified" }>;

const STREET_JUGGLE_SESSIONS: SessionGame = {
  gameId: "street-juggle",
  create: (api, body) => api.createStreetJuggleSession(body),
};

const LANE_NAMES = ["left", "centre", "right"];
const GATE_MESSAGES: Record<Exclude<TouchGate, "ok">, string> = {
  cooldown: "Too soon",
  max_events: "Touch limit reached",
  time_up: "Time is up",
};
const DEFAULT_COLORS: AvatarColors = { skin: FALLBACK_COLOR, hair: "#1c1c22", top: "#ff6a1a" };
const UINT32_MAX = 0xffffffff;

interface ActiveRun {
  config: StreetJuggleConfig;
  rulesetId: string;
  seed: number;
  sim: StreetJuggleSim;
  startMs: number;
}

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function randomSeed(): number {
  return crypto.getRandomValues(new Uint32Array(1))[0] ?? 0;
}

/**
 * Street Juggle (docs/design/p1-second-game.md): touch the spot under the falling ball when it is in the touch
 * zone. `practice` is client-only with a dev tuning panel; `online` plays on an API session with server rewards.
 */
@Component({
  selector: "vybe-street-juggle",
  imports: [RouterLink, AchievementToast],
  templateUrl: "./street-juggle.html",
  styleUrl: "./street-juggle.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    "(window:keydown)": "onKeyDown($event)",
    "(window:keyup)": "onKeyUp($event)",
    "(document:visibilitychange)": "onVisibilityChange()",
  },
})
export class StreetJuggle implements OnInit {
  readonly mode = input<Mode>("practice");
  readonly challenge = input<string | undefined>();
  readonly rematchOf = input<string | undefined>();
  readonly to = input<string | undefined>();

  protected readonly devMode = isDevMode();
  protected readonly tunableKeys = JUGGLE_TUNABLE_KEYS;
  protected readonly laneNames = LANE_NAMES;

  protected readonly phase = signal<Phase>("loading");
  protected readonly error = signal<string | null>(null);
  protected readonly ruleset = signal<JuggleRuleset | null>(null);
  protected readonly secondsLeft = signal(0);
  protected readonly score = signal(0);
  protected readonly streak = signal(0);
  protected readonly lastTouch = signal<JuggleTouchResult | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly summary = signal<Verified | null>(null);
  protected readonly reducedMotion = signal(matchMedia("(prefers-reduced-motion: reduce)").matches);
  protected readonly lanes = computed(() => Array.from({ length: this.ruleset()?.config.lanes ?? 3 }, (_, i) => i));

  protected readonly outcome = signal<RunOutcome | null>(null);
  protected readonly onlineError = signal<string | null>(null);
  protected readonly canRetrySubmit = signal(false);
  protected readonly rejectionText = juggleRejectionText;
  protected readonly noCoinsText = juggleNoCoinsText;
  private readonly verifiedSessionId = signal<string | null>(null);
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

  // Dev tuning panel (practice).
  protected readonly panelOpen = matchMedia("(min-width: 768px)").matches;
  protected readonly overrides = signal<JuggleOverrides>({});
  protected readonly seedInput = signal("");
  protected readonly lastSeed = signal<number | null>(null);
  protected readonly effective = computed((): { config: StreetJuggleConfig | null; problem: string | null } => {
    const ruleset = this.ruleset();
    if (ruleset === null) return { config: null, problem: null };
    try {
      return { config: applyJuggleOverrides(ruleset.config, this.overrides()), problem: null };
    } catch (error: unknown) {
      return { config: null, problem: errorText(error) };
    }
  });
  protected readonly seedProblem = computed(() => {
    const text = this.seedInput().trim();
    if (text === "") return null;
    const seed = Number(text);
    return Number.isInteger(seed) && seed >= 0 && seed <= UINT32_MAX ? null : "Seed must be an integer in [0, 4294967295]";
  });
  protected readonly canStart = computed(
    () => this.mode() === "online" || (this.effective().config !== null && this.seedProblem() === null),
  );
  protected readonly lastTouchLabel = computed(() => {
    const touch = this.lastTouch();
    return touch === null ? "" : touchLabel(touch);
  });

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>("street");
  private readonly stage = viewChild.required<ElementRef<HTMLElement>>("stage");

  private scene: JuggleScene | null = null;
  private readonly api = inject(API);
  private readonly sessions = new OnlineSessions<StreetJuggleConfig>(this.api, verifyJuggleRuleset, undefined, STREET_JUGGLE_SESSIONS);
  private readonly player = inject(PlayerStore);
  private onlineRun: OnlineRun<StreetJuggleConfig> | null = null;
  private run: ActiveRun | null = null;
  private events: JuggleTouch[] = [];
  private flight: Flight | null = null;
  private avatarLane = 1;
  private shownAvatarLane = 1;
  private kickAtMs: number | null = null;
  private flash: { lane: number; kind: JuggleTouchResult["result"]; atMs: number } | null = null;
  private hiddenAtMs: number | null = null;
  private rafId = 0;
  private lastFrameMs = 0;
  /** Ends the run when no frame runs (hidden tab), so the upload still meets its grace window. */
  private endTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const canvas = this.canvas().nativeElement;
      try {
        this.scene = new JuggleScene(canvas, DEFAULT_COLORS);
      } catch (error: unknown) {
        this.fail(`Graphics are not available: ${errorText(error)}`);
        return;
      }
      const observer = new ResizeObserver(() => this.resize());
      observer.observe(this.stage().nativeElement);
      this.resize();
      this.rafId = requestAnimationFrame(this.frame);
      destroyRef.onDestroy(() => {
        cancelAnimationFrame(this.rafId);
        this.clearEndTimer();
        observer.disconnect();
        this.scene = null;
      });
    });
  }

  ngOnInit(): void {
    if (this.mode() === "online") {
      this.phase.set("ready");
      void this.loadAvatarColors();
      return;
    }
    loadJuggleRuleset().then(
      (ruleset) => {
        this.ruleset.set(ruleset);
        if (this.phase() === "loading") this.phase.set("ready");
      },
      (error: unknown) => this.fail(errorText(error)),
    );
  }

  protected start(): void {
    if (this.mode() === "online") {
      void this.startOnline();
      return;
    }
    const ruleset = this.ruleset();
    const { config } = this.effective();
    if (ruleset === null || config === null || this.seedProblem() !== null) return;
    const seedText = this.seedInput().trim();
    this.beginRun(config, ruleset.id, seedText === "" ? randomSeed() : Number(seedText));
  }

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

  /**
   * A spot button: pointer down touches at once (no click delay). Every finger counts: two-thumb play keeps one
   * thumb down while the other taps, and that second touch is not primary. The cadence gate in touch() stops
   * double fires.
   */
  protected onSpotPointerDown(event: PointerEvent, lane: number): void {
    if (!event.isPrimary && event.pointerType !== "touch") return;
    event.preventDefault();
    this.touch(lane);
  }

  /** A spot button activated without a pointer (Enter on a focused button, a switch device, a screen reader). */
  protected onSpotClick(event: MouseEvent, lane: number): void {
    if (event.detail === 0) this.touch(lane);
  }

  protected onKeyDown(event: KeyboardEvent): void {
    if (this.phase() !== "running" || this.isFormField(event.target)) return;
    const lane = laneForKey(event.code, this.run?.config.lanes ?? 3);
    if (lane === null) return;
    event.preventDefault();
    if (!event.repeat) this.touch(lane);
  }

  protected onKeyUp(event: KeyboardEvent): void {
    // Keeps Space from activating a focused button a second time.
    if (this.phase() === "running" && !this.isFormField(event.target) && laneForKey(event.code, 3) !== null) event.preventDefault();
  }

  /**
   * Tab hidden: the browser stops drawing. Practice pauses (the clock moves by the hidden time); an online run
   * cannot pause, because the server clock keeps running (design §6).
   */
  protected onVisibilityChange(): void {
    const now = performance.now();
    if (document.hidden) {
      this.hiddenAtMs = now;
      return;
    }
    const hiddenAt = this.hiddenAtMs;
    this.hiddenAtMs = null;
    if (hiddenAt === null || this.phase() !== "running" || this.run === null) return;
    if (this.mode() === "practice") {
      this.run = { ...this.run, startMs: this.run.startMs + (now - hiddenAt) };
      this.armEndTimer(this.run, this.run.config.durationMs - (now - this.run.startMs));
      this.notice.set("Paused while the tab was hidden");
    } else {
      this.notice.set("The run clock kept running while the tab was hidden");
    }
  }

  protected setOverride(key: JuggleTunableKey, event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    const { [key]: _removed, ...rest } = this.overrides();
    this.overrides.set(text === "" ? rest : { ...rest, [key]: Number(text) });
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected inputChecked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  private async loadAvatarColors(): Promise<void> {
    try {
      const avatar = await this.api.getAvatar();
      this.scene?.setColors({ skin: skinColor(avatar.skinToneId), hair: hairColor(avatar.hairColorId), top: DEFAULT_COLORS.top });
    } catch {
      // The default figure stays; the avatar is decoration, not game state.
    }
  }

  private async startOnline(): Promise<void> {
    this.phase.set("preparing");
    this.outcome.set(null);
    this.onlineError.set(null);
    this.canRetrySubmit.set(false);
    this.summary.set(null);
    this.verifiedSessionId.set(null);
    let online: OnlineRun<StreetJuggleConfig>;
    try {
      online = await this.sessions.prepare(this.challenge());
    } catch (error: unknown) {
      this.onlineError.set(isApiError(error) ? playerMessage(error) : errorText(error));
      this.phase.set("ready");
      return;
    }
    // The server clock started when startSession answered, so local time 0 is now (spec §5).
    this.onlineRun = online;
    this.ruleset.set(online.ruleset);
    this.beginRun(online.ruleset.config, online.ruleset.id, online.seed);
  }

  private beginRun(config: StreetJuggleConfig, rulesetId: string, seed: number): void {
    this.run = { config, rulesetId, seed, sim: new StreetJuggleSim(config, seed), startMs: performance.now() };
    this.events = [];
    this.flight = null;
    this.avatarLane = Math.floor(config.lanes / 2);
    this.shownAvatarLane = this.avatarLane;
    this.kickAtMs = null;
    this.flash = null;
    this.scene?.clearTrail();
    this.score.set(0);
    this.streak.set(0);
    this.lastTouch.set(null);
    this.notice.set(null);
    this.summary.set(null);
    this.lastSeed.set(seed);
    this.secondsLeft.set(Math.ceil(config.durationMs / 1000));
    this.phase.set("running");
    this.armEndTimer(this.run, config.durationMs);
    this.stage().nativeElement.focus();
  }

  /** One touch = one event (design §2.1), scored at once by the shared simulation. */
  private touch(lane: number): void {
    const run = this.run;
    if (run === null || this.phase() !== "running") return;
    const now = performance.now();
    const { sim, config } = run;
    const tMs = touchTime(now - run.startMs, sim.currentTick, config.tickMs);
    const gate = touchGate(config, this.events, tMs);
    if (gate !== "ok") {
      this.notice.set(GATE_MESSAGES[gate]);
      return;
    }
    const event: JuggleTouch = { tMs, lane };
    let result: JuggleTouchResult;
    try {
      result = sim.touch(event);
    } catch (error: unknown) {
      this.fail(errorText(error));
      return;
    }
    this.events = [...this.events, event];
    this.avatarLane = lane;
    this.kickAtMs = now;
    this.flash = { lane, kind: result.result, atMs: now };
    if (result.result !== "whiff") {
      const tick = Math.floor(tMs / config.tickMs);
      this.flight = { fromLane: result.ballLane, toLane: sim.ball.lane, startTick: tick, ticks: flightTicks(config, sim.ball.vy, tick) };
    }
    this.notice.set(null);
    this.lastTouch.set(result);
    this.score.set(sim.score);
    this.streak.set(sim.currentStreak);
  }

  private readonly frame = (now: number): void => {
    this.rafId = requestAnimationFrame(this.frame);
    const dtMs = this.lastFrameMs === 0 ? 0 : Math.min(100, now - this.lastFrameMs);
    this.lastFrameMs = now;
    const run = this.run;
    // Before the first run the street shows with the v1 layout.
    const config = run?.config ?? this.ruleset()?.config ?? fixture.config;
    if (this.phase() === "running" && run !== null) {
      const elapsed = Math.max(0, now - run.startMs);
      if (elapsed >= run.config.durationMs) {
        this.finish(run);
      } else {
        run.sim.advanceTo(Math.floor(elapsed / run.config.tickMs));
        this.secondsLeft.set(Math.ceil((run.config.durationMs - elapsed) / 1000));
        this.streak.set(run.sim.currentStreak);
      }
    }
    // The avatar dashes to its spot; with reduced motion it is already there.
    const step = this.reducedMotion() ? 1 : Math.min(1, dtMs / 40);
    this.shownAvatarLane += (this.avatarLane - this.shownAvatarLane) * step;
    if (this.scene === null) return;
    const sim = this.run?.sim ?? null;
    const ball = sim?.ball ?? null;
    this.scene.render({
      nowMs: now,
      lanes: config.lanes,
      ball:
        ball === null
          ? { lane: 1, y: 0, star: false, inPlay: false }
          : { lane: ballLanePosition(this.flight, ball.lane, sim?.currentTick ?? 0), y: ball.y, star: ball.star, inPlay: ball.inPlay },
      zone: { kickY: config.kickY, goodDy: config.goodDy, perfectDy: config.perfectDy },
      avatarLane: this.shownAvatarLane,
      kickAtMs: this.kickAtMs,
      flash: this.flash,
      reducedMotion: this.reducedMotion(),
    });
  };

  private finish(run: ActiveRun): void {
    if (this.run !== run) return;
    this.clearEndTimer();
    run.sim.advanceTo(run.sim.totalTicks);
    let result: JuggleEvaluateResult;
    try {
      result = evaluateJuggle({
        config: run.config,
        rulesetId: run.rulesetId,
        seed: run.seed,
        session: { status: "active", startedAtMs: 0, submitDeadlineMs: run.config.durationMs + run.config.graceMs },
        submission: { rulesetId: run.rulesetId, submittedAtMs: run.config.durationMs, events: [...this.events] },
      });
    } catch (error: unknown) {
      this.fail(errorText(error));
      return;
    }
    if (result.outcome !== "verified" || result.score !== run.sim.score) {
      this.fail("The local replay does not match the live run; the client recorded an invalid trace");
      return;
    }
    this.summary.set(result);
    this.run = null;
    this.secondsLeft.set(0);
    if (this.mode() === "online") void this.submitOnline();
    else this.phase.set("finished");
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

  private armEndTimer(run: ActiveRun, remainingMs: number): void {
    this.clearEndTimer();
    this.endTimer = setTimeout(() => this.finish(run), Math.max(0, remainingMs) + 50);
  }

  private clearEndTimer(): void {
    if (this.endTimer !== null) clearTimeout(this.endTimer);
    this.endTimer = null;
  }

  private fail(message: string): void {
    this.clearEndTimer();
    this.run = null;
    this.error.set(message);
    this.phase.set("error");
  }

  private isFormField(target: EventTarget | null): boolean {
    return target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement;
  }

  private resize(): void {
    const { clientWidth, clientHeight } = this.stage().nativeElement;
    if (clientWidth > 0 && clientHeight > 0) this.scene?.resize(clientWidth, clientHeight, devicePixelRatio);
  }
}
