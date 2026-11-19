import { ApiError } from "../api/api-client";
import { errorMessage } from "../api/messages";
import {
  type HubErrorCode,
  type JoinErrorCode,
  type KickReason,
  type Look,
  MANIFEST_VERSION,
  type Member,
  PROTOCOL_VERSION,
  type Snapshot,
  type Welcome,
  decodeSnapshot,
  joinErrorCode,
  parseErrorCode,
  parseKickReason,
  parseLook,
  parseMember,
  parseSlot,
  parseWelcome,
} from "./protocol";
import { JOIN_ERROR_TEXT, KICK_RULES, type KickRule, retryDelay, shutdownDelay } from "./reconnect";

/**
 * The plaza connection (docs/design/p2-spaces-protocol.md §2, §4, §5, §8): connect with a fresh ticket, `Join`,
 * hand every server message (checked) to the page, and reconnect by the §8 rules. The hub itself is behind
 * `HubLike`, so the tests and the dev fake hub run without a server.
 */

/** The part of a SignalR `HubConnection` the client uses. */
export interface HubLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  send(method: string, ...args: unknown[]): Promise<void>;
  on(name: string, handler: (...args: unknown[]) => void): void;
  onclose(handler: (error?: Error) => void): void;
}

export type HubFactory = () => HubLike;

export type PlazaStatus =
  | { kind: "connecting" }
  | { kind: "joined" }
  | { kind: "reconnecting"; attempt: number }
  | { kind: "kicked"; reason: KickReason; rule: KickRule; retryAt: number | null }
  /** Stopped: `retry` shows a button; `leave` sends the player to the start page. */
  | { kind: "failed"; message: string; retry: boolean; leave: boolean }
  /** The server speaks another protocol or quick-chat version: reload the page. */
  | { kind: "outdated" }
  | { kind: "closed" };

export interface PlazaEvents {
  status(status: PlazaStatus): void;
  /** A new instance state: on join, after a reconnect, and on `moved` (replace everything). */
  welcome(welcome: Welcome, moved: boolean): void;
  snapshot(snapshot: Snapshot): void;
  joined(member: Member): void;
  left(slot: number): void;
  look(slot: number, look: Look): void;
  emote(slot: number, sku: string): void;
  quickChat(slot: number, id: string): void;
  sticker(slot: number, id: string): void;
  error(code: HubErrorCode): void;
}

export interface Clock {
  now(): number;
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
  random(): number;
}

export const REAL_CLOCK: Clock = {
  now: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  random: () => Math.random(),
};

/** A ticket failure that must stop the retries (§8: 401 or 403 on the ticket request). */
export class TicketRefused extends Error {
  constructor(readonly problem: ApiError) {
    super(problem.code);
    this.name = "TicketRefused";
  }
}

/** Wraps the ticket request: a 401 or 403 becomes `TicketRefused`, so the client knows not to retry. */
export function ticketSource(request: () => Promise<string>): () => Promise<string> {
  return async () => {
    try {
      return await request();
    } catch (error: unknown) {
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) throw new TicketRefused(error);
      throw error;
    }
  };
}

export class PlazaClient {
  private hub: HubLike | null = null;
  private status: PlazaStatus = { kind: "closed" };
  private timer: unknown = null;
  /** Incremented on every new connection: handlers of an old one do nothing. */
  private generation = 0;
  private attempt = 0;
  private lostAt: number | null = null;
  private disposed = false;

  constructor(
    private readonly factory: HubFactory,
    private readonly events: PlazaEvents,
    private readonly clock: Clock = REAL_CLOCK,
    /** The last ticket error (set by the factory's ticket source); read when `start` fails. */
    private readonly lastTicketError: () => unknown = () => null,
  ) {}

  get joined(): boolean {
    return this.status.kind === "joined";
  }

  /** Connects and joins (first entry, or a "Rejoin" / "Try again" button). */
  connect(): void {
    if (this.disposed) return;
    this.clearTimer();
    this.lostAt = null;
    this.attempt = 0;
    this.setStatus({ kind: "connecting" });
    void this.open();
  }

  move(dx: number, dz: number): void {
    this.sendIfJoined("Move", dx, dz);
  }

  emote(sku: string): void {
    this.sendIfJoined("Emote", sku);
  }

  quickChat(id: string): void {
    this.sendIfJoined("QuickChat", id);
  }

  sticker(id: string): void {
    this.sendIfJoined("Sticker", id);
  }

  /** Leaves the instance and closes the connection (page left). No reconnect after this. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.clearTimer();
    const hub = this.hub;
    this.hub = null;
    this.generation++;
    if (hub !== null) {
      // Leave first (no slot reservation), then close; both may fail on a dead socket, which is fine here.
      void hub
        .send("Leave")
        .catch(() => undefined)
        .finally(() => void hub.stop().catch(() => undefined));
    }
    this.setStatus({ kind: "closed" });
  }

  // ------------------------------------------------------------------ connection

  private async open(): Promise<void> {
    const generation = ++this.generation;
    const hub = this.factory();
    this.hub = hub;
    this.listen(hub, generation);
    try {
      await hub.start();
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      this.hub = null;
      const refused = error instanceof TicketRefused ? error : this.lastTicketError();
      if (refused instanceof TicketRefused) {
        this.fail(refused.problem);
        return;
      }
      this.retry();
      return;
    }
    if (generation !== this.generation) {
      void hub.stop().catch(() => undefined);
      return;
    }
    let welcome: Welcome | null;
    try {
      welcome = parseWelcome(await hub.invoke("Join"));
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      this.joinFailed(hub, joinErrorCode(error));
      return;
    }
    if (generation !== this.generation) return;
    if (welcome === null || !this.accept(welcome)) {
      this.stopHub(hub);
      if (welcome === null) this.setStatus({ kind: "failed", message: "The plaza sent data this version of VYBE cannot read. Reload the page.", retry: false, leave: false });
      return;
    }
    this.attempt = 0;
    this.lostAt = null;
    this.setStatus({ kind: "joined" });
    this.events.welcome(welcome, false);
  }

  private listen(hub: HubLike, generation: number): void {
    const on = (name: string, handler: (...args: unknown[]) => void) =>
      hub.on(name, (...args) => {
        if (generation === this.generation && !this.disposed) handler(...args);
      });
    on("snapshot", (tick, avatars) => {
      const snapshot = decodeSnapshot(tick, avatars);
      if (snapshot !== null) this.events.snapshot(snapshot);
    });
    on("joined", (raw) => {
      const member = parseMember(raw);
      if (member !== null) this.events.joined(member);
    });
    on("left", (raw) => {
      const slot = parseSlot(raw);
      if (slot !== null) this.events.left(slot);
    });
    on("look", (rawSlot, rawLook) => {
      const slot = parseSlot(rawSlot);
      const look = parseLook(rawLook);
      if (slot !== null && look !== null) this.events.look(slot, look);
    });
    const expression = (name: string, emit: (slot: number, id: string) => void) =>
      on(name, (rawSlot, id) => {
        const slot = parseSlot(rawSlot);
        if (slot !== null && typeof id === "string" && id.length <= 40) emit(slot, id);
      });
    expression("emote", (slot, sku) => this.events.emote(slot, sku));
    expression("quickChat", (slot, id) => this.events.quickChat(slot, id));
    expression("sticker", (slot, id) => this.events.sticker(slot, id));
    on("moved", (raw) => {
      const welcome = parseWelcome(raw);
      if (welcome !== null && this.accept(welcome)) this.events.welcome(welcome, true);
    });
    on("error", (raw) => {
      const code = parseErrorCode(raw);
      if (code !== null) this.events.error(code); // unknown codes: ignore (§7)
    });
    on("kicked", (raw) => this.kicked(hub, parseKickReason(raw)));
    hub.onclose(() => {
      if (generation !== this.generation || this.disposed) return;
      this.hub = null;
      // Lost without a kick (§8): automatic reconnect with backoff.
      if (this.status.kind === "joined" || this.status.kind === "connecting") {
        this.lostAt = this.clock.now();
        this.attempt = 0;
        this.retry();
      }
    });
  }

  /** Checks the versions (§12); a mismatch stops the client and asks for a reload. */
  private accept(welcome: Welcome): boolean {
    if (welcome.protocolVersion === PROTOCOL_VERSION && welcome.manifestVersion === MANIFEST_VERSION) return true;
    this.generation++;
    const hub = this.hub;
    this.hub = null;
    if (hub !== null) this.stopHub(hub);
    this.setStatus({ kind: "outdated" });
    return false;
  }

  private kicked(hub: HubLike, reason: KickReason | null): void {
    // An unknown reason: the server closes the socket anyway; treat it as a lost connection.
    if (reason === null) return;
    this.generation++;
    this.hub = null;
    this.stopHub(hub);
    const rule = KICK_RULES[reason];
    const now = this.clock.now();
    switch (rule.action.kind) {
      case "auto":
        this.setStatus({ kind: "kicked", reason, rule, retryAt: null });
        this.lostAt = now;
        this.attempt = 1; // after the 2–6 s start, the normal backoff continues
        this.schedule(shutdownDelay(() => this.clock.random()));
        return;
      case "manual":
        this.setStatus({ kind: "kicked", reason, rule, retryAt: now + rule.action.cooldownMs });
        return;
      case "none":
      case "leave":
        this.setStatus({ kind: "kicked", reason, rule, retryAt: null });
        return;
    }
  }

  private joinFailed(hub: HubLike, code: JoinErrorCode | null): void {
    this.hub = null;
    this.generation++;
    this.stopHub(hub);
    if (code === "spaces_full" || code === "rate_limited") {
      this.setStatus({ kind: "failed", message: JOIN_ERROR_TEXT[code], retry: true, leave: false });
      return;
    }
    // `already_joined` or a lost connection during Join: try again with the backoff.
    this.lostAt ??= this.clock.now();
    this.retry();
  }

  private retry(): void {
    const now = this.clock.now();
    this.lostAt ??= now;
    const delay = retryDelay(this.attempt, now - this.lostAt, () => this.clock.random());
    if (delay === null) {
      this.setStatus({ kind: "failed", message: "VYBE could not reach the plaza. Check your connection and try again.", retry: true, leave: false });
      return;
    }
    this.setStatus({ kind: "reconnecting", attempt: this.attempt + 1 });
    this.attempt++;
    this.schedule(delay);
  }

  private schedule(delay: number): void {
    this.clearTimer();
    this.timer = this.clock.setTimeout(() => {
      this.timer = null;
      if (!this.disposed) void this.open();
    }, delay);
  }

  private fail(error: ApiError): void {
    const leave = error.status === 401;
    this.setStatus({ kind: "failed", message: errorMessage(error), retry: !leave && error.code !== "account_suspended", leave });
  }

  private sendIfJoined(method: string, ...args: unknown[]): void {
    const hub = this.hub;
    if (hub === null || this.status.kind !== "joined") return;
    // A failed send means the socket is closing; `onclose` starts the reconnect, so nothing to do here.
    void hub.send(method, ...args).catch(() => undefined);
  }

  private stopHub(hub: HubLike): void {
    void hub.stop().catch(() => undefined);
  }

  private clearTimer(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
  }

  private setStatus(status: PlazaStatus): void {
    this.status = status;
    this.events.status(status);
  }
}
