import { describe, expect, it } from "vitest";
import { ApiError } from "../api/api-client";
import { FakeHub } from "./fake-hub";
import { type Clock, type HubLike, JOIN_TIMEOUT_MS, PlazaClient, type PlazaEvents, type PlazaStatus, TicketRefused, ticketSource } from "./plaza-client";
import { MANIFEST_VERSION, type Welcome } from "./protocol";

const look = {
  bodyPresetId: "body_a",
  skinToneId: "skin_02",
  facePresetId: "face_01",
  hairPresetId: "hair_short",
  hairColorId: "black",
  equipped: { hair: null, top: null, bottom: null, shoes: null, emote: null, accessories: [] },
};
const WELCOME = {
  protocolVersion: 1,
  manifestVersion: MANIFEST_VERSION,
  instanceId: 3,
  slot: 0,
  tickHz: 10,
  bounds: { minX: -1600, maxX: 1600, minZ: -1600, maxZ: 1600 },
  speedCmPerS: 350,
  members: [{ slot: 0, handle: "me", look }],
};

/** A scripted hub: each connection follows `script`, and the test pushes server messages. */
class StubHub implements HubLike {
  readonly handlers = new Map<string, (...args: unknown[]) => void>();
  close: ((error?: Error) => void) | null = null;
  readonly sent: unknown[][] = [];
  stopped = false;

  constructor(private readonly script: { start?: () => Promise<void>; join?: () => Promise<unknown> }) {}

  start(): Promise<void> {
    return this.script.start?.() ?? Promise.resolve();
  }
  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }
  invoke(method: string): Promise<unknown> {
    expect(method).toBe("Join");
    return this.script.join?.() ?? Promise.resolve(WELCOME);
  }
  send(method: string, ...args: unknown[]): Promise<void> {
    this.sent.push([method, ...args]);
    return Promise.resolve();
  }
  on(name: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(name, handler);
  }
  onclose(handler: (error?: Error) => void): void {
    this.close = handler;
  }
  push(name: string, ...args: unknown[]): void {
    this.handlers.get(name)?.(...args);
  }
}

class ManualClock implements Clock {
  time = 0;
  timers: { at: number; fn: () => void; id: number }[] = [];
  private id = 0;
  now(): number {
    return this.time;
  }
  setTimeout(fn: () => void, ms: number): unknown {
    const id = ++this.id;
    this.timers.push({ at: this.time + ms, fn, id });
    return id;
  }
  clearTimeout(handle: unknown): void {
    this.timers = this.timers.filter((t) => t.id !== handle);
  }
  random(): number {
    return 0.5;
  }
  async advance(ms: number): Promise<void> {
    this.time += ms;
    const due = this.timers.filter((t) => t.at <= this.time);
    this.timers = this.timers.filter((t) => t.at > this.time);
    for (const t of due) t.fn();
    await flush();
  }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function setup(scripts: ConstructorParameters<typeof StubHub>[0][] = [{}]) {
  const hubs: StubHub[] = [];
  const statuses: PlazaStatus[] = [];
  const welcomes: [Welcome, boolean][] = [];
  const log: string[] = [];
  const events: PlazaEvents = {
    status: (s) => statuses.push(s),
    welcome: (w, moved) => welcomes.push([w, moved]),
    snapshot: (s) => log.push(`snapshot ${s.tick} ${s.avatars.length}`),
    joined: (m) => log.push(`joined ${m.slot}`),
    left: (slot) => log.push(`left ${slot}`),
    look: (slot) => log.push(`look ${slot}`),
    emote: (slot, sku) => log.push(`emote ${slot} ${sku}`),
    quickChat: (slot, id) => log.push(`quickChat ${slot} ${id}`),
    sticker: (slot, id) => log.push(`sticker ${slot} ${id}`),
    error: (code) => log.push(`error ${code}`),
  };
  const clock = new ManualClock();
  const client = new PlazaClient(
    () => {
      const hub = new StubHub(scripts[Math.min(hubs.length, scripts.length - 1)]!);
      hubs.push(hub);
      return hub;
    },
    events,
    clock,
  );
  const kinds = () => statuses.map((s) => s.kind);
  return { client, hubs, statuses, kinds, welcomes, log, clock };
}

describe("plaza client", () => {
  it("connects, joins, and hands checked server messages on", async () => {
    const { client, hubs, kinds, welcomes, log } = setup();
    client.connect();
    await flush();
    expect(kinds()).toEqual(["connecting", "joined"]);
    expect(welcomes[0]?.[0].instanceId).toBe(3);
    const hub = hubs[0]!;
    hub.push("snapshot", 10, [0, 1, 2, 3, 1]);
    hub.push("snapshot", "bad", [0, 1, 2, 3, 1]);
    hub.push("quickChat", 4, "hi");
    hub.push("quickChat", 99, "hi");
    hub.push("left", 4);
    hub.push("error", "rate_limited");
    hub.push("error", "brand_new_code");
    expect(log).toEqual(["snapshot 10 1", "quickChat 4 hi", "left 4", "error rate_limited"]);
    client.move(0, 127);
    client.quickChat("gg");
    expect(hub.sent).toEqual([
      ["Move", 0, 127],
      ["QuickChat", "gg"],
    ]);
  });

  it("replaces the scene on `moved`", async () => {
    const { client, hubs, welcomes } = setup();
    client.connect();
    await flush();
    hubs[0]!.push("moved", { ...WELCOME, instanceId: 4, slot: 1, members: [{ slot: 1, handle: null, look }] });
    expect(welcomes.map(([w, moved]) => [w.instanceId, moved])).toEqual([
      [3, false],
      [4, true],
    ]);
  });

  it("reconnects with backoff after a lost connection, and joins again", async () => {
    const { client, hubs, kinds, clock, welcomes } = setup([{}, { start: () => Promise.reject(new Error("offline")) }, {}]);
    client.connect();
    await flush();
    hubs[0]!.close?.(new Error("lost"));
    await flush();
    expect(kinds().at(-1)).toBe("reconnecting");
    await clock.advance(0); // attempt 1 after 0 s fails
    expect(hubs).toHaveLength(2);
    expect(kinds().at(-1)).toBe("reconnecting");
    await clock.advance(1999);
    expect(hubs).toHaveLength(2);
    await clock.advance(1); // attempt 2 after 2 s
    expect(hubs).toHaveLength(3);
    expect(kinds().at(-1)).toBe("joined");
    expect(welcomes).toHaveLength(2);
  });

  it("drops snapshot positions outside the Welcome bounds", async () => {
    const { client, hubs, log } = setup([{ join: () => Promise.resolve({ ...WELCOME, bounds: { minX: -500, maxX: 500, minZ: -500, maxZ: 500 } }) }]);
    client.connect();
    await flush();
    hubs[0]!.push("snapshot", 1, [0, 400, 0, 0, 0, 1, 900, 0, 0, 0]);
    expect(log).toEqual(["snapshot 1 1"]);
  });

  it("a drop during Join starts one retry, at once", async () => {
    let rejectJoin: (error: Error) => void = () => undefined;
    const { client, hubs, kinds, clock } = setup([{ join: () => new Promise((_, reject) => (rejectJoin = reject)) }, {}]);
    client.connect();
    await flush();
    hubs[0]!.close?.(new Error("lost"));
    rejectJoin(new Error("connection closed"));
    await flush();
    expect(kinds()).toEqual(["connecting", "reconnecting"]);
    await clock.advance(0);
    expect(hubs).toHaveLength(2);
    expect(kinds().at(-1)).toBe("joined");
  });

  it("a drop during the Join of a retry attempt keeps the backoff going", async () => {
    let rejectJoin: (error: Error) => void = () => undefined;
    const { client, hubs, kinds, clock } = setup([{}, { join: () => new Promise((_, reject) => (rejectJoin = reject)) }, {}]);
    client.connect();
    await flush();
    hubs[0]!.close?.(new Error("lost"));
    await clock.advance(0); // attempt 1: connects, waits on Join
    expect(hubs).toHaveLength(2);
    hubs[1]!.close?.(new Error("lost again"));
    rejectJoin(new Error("connection closed"));
    await flush();
    expect(kinds().at(-1)).toBe("reconnecting");
    await clock.advance(2000); // attempt 2 after 2 s
    expect(hubs).toHaveLength(3);
    expect(kinds().at(-1)).toBe("joined");
  });

  it("a Join without an answer times out and reconnects", async () => {
    const { client, hubs, kinds, clock } = setup([{ join: () => new Promise(() => undefined) }, {}]);
    client.connect();
    await flush();
    await clock.advance(JOIN_TIMEOUT_MS - 1);
    expect(kinds()).toEqual(["connecting"]);
    await clock.advance(1);
    expect(hubs[0]!.stopped).toBe(true);
    expect(kinds().at(-1)).toBe("reconnecting");
    await clock.advance(0);
    expect(hubs).toHaveLength(2);
    expect(kinds().at(-1)).toBe("joined");
  });

  it("gives up after 5 minutes and shows a retry", async () => {
    const { client, hubs, statuses, clock } = setup([{}, { start: () => Promise.reject(new Error("offline")) }]);
    client.connect();
    await flush();
    hubs[0]!.close?.();
    for (let i = 0; i < 40; i++) await clock.advance(30_000);
    const last = statuses.at(-1);
    expect(last).toMatchObject({ kind: "failed", retry: true });
    expect(hubs.length).toBeLessThan(20);
  });

  it("does not retry when the ticket is refused (401/403)", async () => {
    const refused = new TicketRefused(new ApiError(403, "account_suspended"));
    const { client, statuses, clock, hubs } = setup([{ start: () => Promise.reject(refused) }]);
    client.connect();
    await flush();
    await clock.advance(60_000);
    expect(hubs).toHaveLength(1);
    expect(statuses.at(-1)).toMatchObject({ kind: "failed", retry: false, message: "Your account is suspended." });
  });

  it("wraps 401 and 403 ticket errors only", async () => {
    await expect(ticketSource(() => Promise.reject(new ApiError(401, "unauthenticated")))()).rejects.toBeInstanceOf(TicketRefused);
    await expect(ticketSource(() => Promise.reject(new ApiError(429, "rate_limited")))()).rejects.toBeInstanceOf(ApiError);
    await expect(ticketSource(() => Promise.resolve("t"))()).resolves.toBe("t");
  });

  it("shows spaces_full with a retry button and does not loop", async () => {
    const { client, statuses, clock, hubs } = setup([{ join: () => Promise.reject(new Error("HubException: spaces_full")) }]);
    client.connect();
    await flush();
    await clock.advance(60_000);
    expect(hubs).toHaveLength(1);
    expect(statuses.at(-1)).toMatchObject({ kind: "failed", retry: true, message: "All plazas are full right now. Try again in a few minutes." });
  });

  it("asks for a reload when the server speaks another version", async () => {
    const { client, kinds } = setup([{ join: () => Promise.resolve({ ...WELCOME, manifestVersion: MANIFEST_VERSION + 1 }) }]);
    client.connect();
    await flush();
    expect(kinds().at(-1)).toBe("outdated");
  });

  it("server_shutdown: reconnects by itself after 2–6 s", async () => {
    const { client, hubs, kinds, clock } = setup();
    client.connect();
    await flush();
    hubs[0]!.push("kicked", "server_shutdown");
    hubs[0]!.close?.();
    expect(hubs[0]!.stopped).toBe(true);
    expect(kinds().at(-1)).toBe("kicked");
    await clock.advance(3_999);
    expect(hubs).toHaveLength(1);
    await clock.advance(1); // random 0.5 → 4 s
    expect(hubs).toHaveLength(2);
    expect(kinds().at(-1)).toBe("joined");
  });

  it("spaces_full kick: reconnects by itself after 60 s; slow_client: at once", async () => {
    for (const [reason, wait] of [
      ["spaces_full", 60_000],
      ["slow_client", 0],
    ] as const) {
      const { client, hubs, clock } = setup();
      client.connect();
      await flush();
      hubs[0]!.push("kicked", reason);
      if (wait > 0) {
        await clock.advance(wait - 1);
        expect(hubs).toHaveLength(1);
      }
      await clock.advance(1);
      expect(hubs).toHaveLength(2);
    }
  });

  it("Join fails with account_unavailable: no retry, the page leaves", async () => {
    const { client, statuses, clock, hubs } = setup([{ join: () => Promise.reject(new Error("HubException: account_unavailable")) }]);
    client.connect();
    await flush();
    await clock.advance(60_000);
    expect(hubs).toHaveLength(1);
    expect(statuses.at(-1)).toMatchObject({ kind: "kicked", reason: "account_unavailable", rule: { action: { kind: "leave" } } });
  });

  it("replaced, idle and abuse kicks never reconnect by themselves", async () => {
    for (const reason of ["replaced", "idle", "rate_limited", "suspended", "account_unavailable"]) {
      const { client, hubs, statuses, clock } = setup();
      client.connect();
      await flush();
      hubs[0]!.push("kicked", reason);
      hubs[0]!.close?.();
      await clock.advance(10 * 60_000);
      expect(hubs).toHaveLength(1);
      expect(statuses.at(-1)).toMatchObject({ kind: "kicked", reason });
    }
  });

  it("rate_limited kick: the rejoin button waits 60 s", async () => {
    const { client, hubs, statuses, clock } = setup();
    clock.time = 1_000;
    client.connect();
    await flush();
    hubs[0]!.push("kicked", "rate_limited");
    expect(statuses.at(-1)).toMatchObject({ kind: "kicked", retryAt: 61_000 });
  });

  it("sends Leave and closes on dispose, and ignores late messages", async () => {
    const { client, hubs, log, statuses } = setup();
    client.connect();
    await flush();
    client.dispose();
    await flush();
    expect(hubs[0]!.sent).toEqual([["Leave"]]);
    expect(hubs[0]!.stopped).toBe(true);
    hubs[0]!.push("quickChat", 1, "hi");
    expect(log).toEqual([]);
    expect(statuses.at(-1)?.kind).toBe("closed");
    client.move(1, 1);
    expect(hubs[0]!.sent).toHaveLength(1);
  });

  it("sends nothing before Join", async () => {
    const { client, hubs } = setup([{ join: () => new Promise(() => undefined) }]);
    client.connect();
    await flush();
    client.move(0, 127);
    expect(hubs[0]!.sent).toEqual([]);
  });
});

describe("fake hub (dev flag)", () => {
  it("welcomes, moves by the server rule, and rate-limits expressions", async () => {
    const hub = new FakeHub({ bots: 3, seed: 1, lively: false, handle: "you", instanceId: 3 });
    const snapshots: number[][] = [];
    const errors: unknown[] = [];
    hub.on("snapshot", (_tick, flat) => snapshots.push(flat as number[]));
    hub.on("error", (code) => errors.push(code));
    await hub.start();
    const welcome = (await hub.invoke("Join")) as Welcome;
    expect(welcome.members).toHaveLength(4);
    await hub.send("QuickChat", "hi");
    await hub.send("QuickChat", "gg");
    await hub.send("Sticker", "nope");
    expect(errors).toEqual(["rate_limited", "invalid_input"]);
    await hub.stop();
  });
});
