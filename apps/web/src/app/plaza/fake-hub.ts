import catalog from "../../../../../assets/manifests/catalog.v1.json";
import type { HubLike } from "./plaza-client";
import { DEFAULT_BOUNDS, DEFAULT_SPEED_CM_PER_S, type Look, MANIFEST_VERSION, type Member, PHRASES, PROTOCOL_VERSION, STICKERS, type Welcome } from "./protocol";

/**
 * A local stand-in for the spaces hub (dev builds only: `/plaza?fake=1`, and the unit tests). It follows the
 * server rules of docs/design/p2-spaces-protocol.md closely enough to test the page without stream V: 10 Hz
 * snapshots (1 Hz when nothing moves), the §4 move rule with the 500 ms stale intent, 1 expression per 2 s,
 * bots that walk and talk. Deterministic for a seed, so screenshots are stable.
 */

export interface FakeHubOptions {
  /** Other avatars in the instance (0–15). */
  bots: number;
  seed: number;
  /** Bots walk and talk; false keeps them still (screenshots). */
  lively: boolean;
  /** Own handle; null = private profile. */
  handle: string | null;
  instanceId: number;
}

export const FAKE_DEFAULTS: FakeHubOptions = { bots: 5, seed: 7, lively: true, handle: "you", instanceId: 3 };

const SKUS = (slot: string): string[] => catalog.items.filter((i) => i.slot === slot).map((i) => i.sku);
const NAMES = ["ana_hoops", "milo", "zed_k", null, "rae", "juno_77", "kai", "lumi", "otto_b", null, "nova", "pip", "sol_r", "bex", "ivo"];
const EMOTES = SKUS("emote");

/** Small seeded PRNG (mulberry32). */
function prng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function pick<T>(random: () => number, list: readonly T[]): T {
  return list[Math.floor(random() * list.length)]!;
}

function randomLook(random: () => number): Look {
  const maybe = (slot: string, chance: number) => (random() < chance ? pick(random, SKUS(slot)) : null);
  return {
    bodyPresetId: pick(random, ["body_a", "body_b"]),
    skinToneId: pick(random, ["skin_01", "skin_02", "skin_03", "skin_04", "skin_05", "skin_06"]),
    facePresetId: pick(random, ["face_01", "face_02", "face_03", "face_04"]),
    hairPresetId: pick(random, ["hair_short", "hair_long", "hair_curly", "hair_buzz"]),
    hairColorId: pick(random, ["black", "brown", "blond", "red", "blue"]),
    equipped: {
      hair: maybe("hair", 0.3),
      top: pick(random, SKUS("top")),
      bottom: pick(random, SKUS("bottom")),
      shoes: pick(random, SKUS("shoes")),
      emote: null,
      accessories: [maybe("accessory", 0.4), null],
    },
  };
}

interface Avatar {
  member: Member;
  x: number;
  z: number;
  heading: number;
  state: 0 | 1 | 2;
  dx: number;
  dz: number;
  intentAt: number;
  /** Bot: time of the next decision. */
  next: number;
}

type Handler = (...args: unknown[]) => void;

export class FakeHub implements HubLike {
  private readonly handlers = new Map<string, Handler[]>();
  private readonly closeHandlers: ((error?: Error) => void)[] = [];
  private readonly avatars = new Map<number, Avatar>();
  private readonly lastExpression = new Map<string, number>();
  private readonly random: () => number;
  private timer: ReturnType<typeof setInterval> | null = null;
  private tickCount = 1000;
  private time = 0;
  private ownSlot = 0;
  private joined = false;
  private nextKey = 1;
  private instanceId = 0;
  private lastSafety = Number.NEGATIVE_INFINITY;

  constructor(private readonly options: FakeHubOptions = FAKE_DEFAULTS) {
    this.random = prng(options.seed);
  }

  start(): Promise<void> {
    this.timer = setInterval(() => this.tick(), 100);
    return Promise.resolve();
  }

  stop(): Promise<void> {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
    const handlers = [...this.closeHandlers];
    this.closeHandlers.length = 0;
    for (const handler of handlers) handler();
    return Promise.resolve();
  }

  on(name: string, handler: Handler): void {
    this.handlers.set(name, [...(this.handlers.get(name) ?? []), handler]);
  }

  onclose(handler: (error?: Error) => void): void {
    this.closeHandlers.push(handler);
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    const name = method.toLowerCase();
    if (name === "report" || name === "block") return this.safety(name, args);
    if (name !== "join") return Promise.reject(new Error("unknown method"));
    if (this.joined) return Promise.reject(new Error("HubException: already_joined"));
    this.joined = true;
    this.instanceId = this.options.instanceId;
    this.populate();
    return Promise.resolve(this.welcome());
  }

  /** `Report` and `Block` as the server checks them (protocol doc §9); a block moves this player to a new instance. */
  private safety(name: "report" | "block", args: unknown[]): Promise<unknown> {
    const [slot, key, reason] = args;
    if (!this.joined) return Promise.reject(new Error("HubException: not_joined"));
    if (name === "report" && !["offensive_name", "cheating", "harassment", "other"].includes(reason as string)) {
      return Promise.reject(new Error("HubException: invalid_input"));
    }
    if (this.time - this.lastSafety < 1000) return Promise.reject(new Error("HubException: rate_limited"));
    this.lastSafety = this.time;
    const target = this.avatars.get(slot as number);
    if (target === undefined || slot === this.ownSlot || target.member.key !== key) return Promise.reject(new Error("HubException: not_found"));
    if (name === "block") {
      this.avatars.delete(slot as number);
      this.instanceId++;
      this.emit("moved", this.welcome());
    }
    return Promise.resolve(null);
  }

  send(method: string, ...args: unknown[]): Promise<void> {
    const own = this.avatars.get(this.ownSlot);
    if (!this.joined || own === undefined) {
      this.emit("error", "not_joined");
      return Promise.resolve();
    }
    switch (method.toLowerCase()) {
      case "move": {
        const [dx, dz] = args;
        if (!Number.isInteger(dx) || !Number.isInteger(dz)) return this.invalid();
        own.dx = Math.max(-127, Math.min(127, dx as number));
        own.dz = Math.max(-127, Math.min(127, dz as number));
        own.intentAt = this.time;
        return Promise.resolve();
      }
      case "emote":
      case "quickchat":
      case "sticker": {
        const [id] = args;
        if (typeof id !== "string") return this.invalid();
        const valid =
          method.toLowerCase() === "emote" ? EMOTES.includes(id) : method.toLowerCase() === "quickchat" ? PHRASES.some((p) => p.id === id) : STICKERS.some((s) => s.id === id);
        if (!valid) return this.invalid();
        const last = this.lastExpression.get(method) ?? Number.NEGATIVE_INFINITY;
        if (this.time - last < 2000) {
          this.emit("error", "rate_limited");
          return Promise.resolve();
        }
        this.lastExpression.set(method, this.time);
        const name = method.toLowerCase() === "quickchat" ? "quickChat" : method.toLowerCase();
        this.emit(name, this.ownSlot, id);
        return Promise.resolve();
      }
      case "leave":
        this.joined = false;
        return Promise.resolve();
      default:
        return this.invalid();
    }
  }

  /** Test helpers: what the server would push. */
  push(name: string, ...args: unknown[]): void {
    this.emit(name, ...args);
  }

  /** Simulates a dropped socket (no kick). */
  drop(): void {
    void this.stop();
  }

  private invalid(): Promise<void> {
    this.emit("error", "invalid_input");
    return Promise.resolve();
  }

  private emit(name: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(name) ?? []) handler(...args);
  }

  private populate(): void {
    const look = randomLook(this.random);
    this.add(this.ownSlot, { slot: this.ownSlot, key: this.nextKey++, handle: this.options.handle, look }, 0, 0);
    const bots = Math.max(0, Math.min(15, this.options.bots));
    for (let i = 0; i < bots; i++) {
      const slot = i + 1;
      // Bots stand on a ring in front of the camera, so a still screenshot shows them all.
      const angle = (i / Math.max(bots, 1)) * Math.PI * 1.2 - Math.PI * 0.6;
      const radius = 280 + (i % 3) * 140;
      this.add(slot, { slot, key: this.nextKey++, handle: NAMES[i % NAMES.length] ?? null, look: randomLook(this.random) }, Math.round(Math.sin(angle) * radius), Math.round(Math.cos(angle) * radius) + 250);
    }
  }

  private add(slot: number, member: Member, x: number, z: number): void {
    const heading = Math.round(((Math.atan2(-x, -z) + 2 * Math.PI) % (2 * Math.PI)) * (256 / (2 * Math.PI))) % 256;
    this.avatars.set(slot, { member, x, z, heading: slot === this.ownSlot ? 0 : heading, state: 0, dx: 0, dz: 0, intentAt: 0, next: 0 });
  }

  private welcome(): Welcome {
    return {
      protocolVersion: PROTOCOL_VERSION,
      manifestVersion: MANIFEST_VERSION,
      instanceId: this.instanceId,
      slot: this.ownSlot,
      tickHz: 10,
      bounds: DEFAULT_BOUNDS,
      speedCmPerS: DEFAULT_SPEED_CM_PER_S,
      members: [...this.avatars.values()].map((a) => a.member),
    };
  }

  private tick(): void {
    this.time += 100;
    this.tickCount++;
    if (!this.joined) return;
    let changed = false;
    for (const [slot, avatar] of this.avatars) {
      if (slot !== this.ownSlot && this.options.lively) this.think(avatar);
      if (this.time - avatar.intentAt > 500 && slot === this.ownSlot) {
        avatar.dx = 0;
        avatar.dz = 0;
      }
      const length = Math.hypot(avatar.dx, avatar.dz);
      const state = length >= 16 ? 1 : 0;
      if (state !== avatar.state) changed = true;
      avatar.state = state;
      if (state === 1) {
        const step = DEFAULT_SPEED_CM_PER_S * Math.min(length / 127, 1) * 0.1;
        avatar.x = Math.round(Math.max(-1600, Math.min(1600, avatar.x + (avatar.dx / length) * step)));
        avatar.z = Math.round(Math.max(-1600, Math.min(1600, avatar.z + (avatar.dz / length) * step)));
        avatar.heading = Math.round(((Math.atan2(avatar.dx, avatar.dz) + 2 * Math.PI) % (2 * Math.PI)) * (256 / (2 * Math.PI))) % 256;
        changed = true;
      }
    }
    if (changed || this.tickCount % 10 === 0) {
      const flat: number[] = [];
      for (const [slot, a] of this.avatars) flat.push(slot, a.x, a.z, a.heading, a.state);
      this.emit("snapshot", this.tickCount, flat);
    }
  }

  private think(bot: Avatar): void {
    if (this.time < bot.next) return;
    bot.next = this.time + 1500 + this.random() * 3000;
    const roll = this.random();
    const slot = bot.member.slot;
    if (roll < 0.45) {
      const angle = this.random() * Math.PI * 2;
      // Walk back toward the middle when far out.
      const home = Math.hypot(bot.x, bot.z) > 900 ? Math.atan2(-bot.x, -bot.z) : angle;
      bot.dx = Math.round(Math.sin(home) * 127);
      bot.dz = Math.round(Math.cos(home) * 127);
    } else {
      bot.dx = 0;
      bot.dz = 0;
      if (roll < 0.6) this.emit("quickChat", slot, pick(this.random, PHRASES).id);
      else if (roll < 0.7) this.emit("sticker", slot, pick(this.random, STICKERS).id);
      else if (roll < 0.78) this.emit("emote", slot, pick(this.random, EMOTES));
    }
  }
}
