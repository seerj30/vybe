import quickchat from "../../../../../assets/manifests/quickchat.v1.json";

/**
 * P2 spaces protocol, client side (docs/design/p2-spaces-protocol.md). Every value from the hub is untrusted:
 * the parse functions check shapes and ranges at this boundary, and return null for a message to drop.
 */

export const PROTOCOL_VERSION = 1;
export const HUB_URL = "/api/v1/spaces/hub";
export const MAX_AVATARS = 16;
export const TICK_MS = 100;
/** §3 bounds (cm) and speed; the Welcome repeats them, and the client uses the Welcome values. */
export const DEFAULT_BOUNDS: Bounds = { minX: -1600, maxX: 1600, minZ: -1600, maxZ: 1600 };
export const DEFAULT_SPEED_CM_PER_S = 350;
/** §4: a Move component is −127…127; below 16 (vector length) the server stops the avatar. */
export const MOVE_MAX = 127;
export const MOVE_DEAD_ZONE = 16;

export interface Bounds {
  minX: number;
  maxX: number;
  minZ: number;
  maxZ: number;
}

export interface Equipped {
  hair: string | null;
  top: string | null;
  bottom: string | null;
  shoes: string | null;
  emote: string | null;
  accessories: (string | null)[];
}

/** §5 Look: avatar presets and equipped item SKUs. */
export interface Look {
  bodyPresetId: string;
  skinToneId: string;
  facePresetId: string;
  hairPresetId: string;
  hairColorId: string;
  equipped: Equipped;
}

export interface Member {
  slot: number;
  /** Null for a private profile (decision 6): the client shows a generic name. */
  handle: string | null;
  look: Look;
}

export interface Welcome {
  protocolVersion: number;
  manifestVersion: number;
  instanceId: number;
  slot: number;
  tickHz: number;
  bounds: Bounds;
  speedCmPerS: number;
  members: Member[];
}

/** 0 idle, 1 walking, 2 away (connection lost, slot reserved). */
export type AvatarState = 0 | 1 | 2;

export interface AvatarSample {
  slot: number;
  x: number;
  z: number;
  heading: number;
  state: AvatarState;
}

export interface Snapshot {
  tick: number;
  avatars: AvatarSample[];
}

export type KickReason =
  | "server_shutdown"
  | "replaced"
  | "idle"
  | "not_joined"
  | "rate_limited"
  | "invalid_input"
  | "suspended"
  | "account_unavailable";

export type HubErrorCode = "rate_limited" | "invalid_input" | "not_joined";
export type JoinErrorCode = "spaces_full" | "already_joined" | "rate_limited";

const KICK_REASONS: ReadonlySet<string> = new Set<KickReason>([
  "server_shutdown",
  "replaced",
  "idle",
  "not_joined",
  "rate_limited",
  "invalid_input",
  "suspended",
  "account_unavailable",
]);
const ERROR_CODES: ReadonlySet<string> = new Set<HubErrorCode>(["rate_limited", "invalid_input", "not_joined"]);
const JOIN_ERRORS: readonly JoinErrorCode[] = ["spaces_full", "already_joined", "rate_limited"];

// ------------------------------------------------------------------ quick-chat manifest

export interface Phrase {
  id: string;
  text: string;
}

export interface Sticker {
  id: string;
  label: string;
}

export const MANIFEST_VERSION: number = quickchat.version;
export const PHRASES: readonly Phrase[] = quickchat.phrases;
export const STICKERS: readonly Sticker[] = quickchat.stickers;
const PHRASE_TEXT: ReadonlyMap<string, string> = new Map(PHRASES.map((p) => [p.id, p.text]));
const STICKER_LABEL: ReadonlyMap<string, string> = new Map(STICKERS.map((s) => [s.id, s.label]));

/** The text of a phrase id, or undefined for an id this build does not know (drop it). */
export function phraseText(id: string): string | undefined {
  return PHRASE_TEXT.get(id);
}

export function stickerLabel(id: string): string | undefined {
  return STICKER_LABEL.get(id);
}

/** Shown for a member without a handle (private profile, decision 6). */
export const GENERIC_NAME = "Player";

export function displayName(member: Pick<Member, "handle">): string {
  return member.handle ?? GENERIC_NAME;
}

// ------------------------------------------------------------------ parsing

type Obj = Record<string, unknown>;

function isObj(value: unknown): value is Obj {
  return typeof value === "object" && value !== null && !Array.isArray(value) && !ArrayBuffer.isView(value);
}

function isInt(value: unknown, min: number, max: number): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= min && value <= max;
}

/** A short id (SKU, preset, handle): letters, digits, `_`, `-`, `.`; never shown as markup. */
function isId(value: unknown, max = 40): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= max && /^[A-Za-z0-9_.-]+$/.test(value);
}

function idOrNull(value: unknown): string | null | undefined {
  if (value === null || value === undefined) return null;
  return isId(value) ? value : undefined;
}

function num(value: unknown): number | null {
  // MessagePack may decode a uint32 or an int64 as a bigint.
  if (typeof value === "bigint") return value <= BigInt(Number.MAX_SAFE_INTEGER) && value >= BigInt(Number.MIN_SAFE_INTEGER) ? Number(value) : null;
  return typeof value === "number" ? value : null;
}

export function parseLook(value: unknown): Look | null {
  if (!isObj(value)) return null;
  const { bodyPresetId, skinToneId, facePresetId, hairPresetId, hairColorId, equipped } = value;
  if (!isId(bodyPresetId) || !isId(skinToneId) || !isId(facePresetId) || !isId(hairPresetId) || !isId(hairColorId)) return null;
  if (!isObj(equipped)) return null;
  const slots = ["hair", "top", "bottom", "shoes", "emote"] as const;
  const parsed: Partial<Record<(typeof slots)[number], string | null>> = {};
  for (const slot of slots) {
    const sku = idOrNull(equipped[slot]);
    if (sku === undefined) return null;
    parsed[slot] = sku;
  }
  const rawAccessories = equipped["accessories"] ?? [];
  if (!Array.isArray(rawAccessories) || rawAccessories.length > 2) return null;
  const accessories: (string | null)[] = [];
  for (const item of rawAccessories) {
    const sku = idOrNull(item);
    if (sku === undefined) return null;
    accessories.push(sku);
  }
  return {
    bodyPresetId,
    skinToneId,
    facePresetId,
    hairPresetId,
    hairColorId,
    equipped: {
      hair: parsed.hair ?? null,
      top: parsed.top ?? null,
      bottom: parsed.bottom ?? null,
      shoes: parsed.shoes ?? null,
      emote: parsed.emote ?? null,
      accessories,
    },
  };
}

export function parseMember(value: unknown): Member | null {
  if (!isObj(value)) return null;
  const slot = num(value["slot"]);
  const handle = value["handle"];
  const look = parseLook(value["look"]);
  if (!isInt(slot, 0, MAX_AVATARS - 1) || look === null) return null;
  if (handle !== null && handle !== undefined && !isId(handle, 32)) return null;
  return { slot, handle: typeof handle === "string" ? handle : null, look };
}

function parseBounds(value: unknown): Bounds | null {
  if (!isObj(value)) return null;
  const b = { minX: num(value["minX"]), maxX: num(value["maxX"]), minZ: num(value["minZ"]), maxZ: num(value["maxZ"]) };
  if (b.minX === null || b.maxX === null || b.minZ === null || b.maxZ === null) return null;
  if (!(b.minX < b.maxX && b.minZ < b.maxZ) || Math.max(Math.abs(b.minX), Math.abs(b.maxX), Math.abs(b.minZ), Math.abs(b.maxZ)) > 100_000) return null;
  return { minX: b.minX, maxX: b.maxX, minZ: b.minZ, maxZ: b.maxZ };
}

export function parseWelcome(value: unknown): Welcome | null {
  if (!isObj(value)) return null;
  const protocolVersion = num(value["protocolVersion"]);
  const manifestVersion = num(value["manifestVersion"]);
  const instanceId = num(value["instanceId"]);
  const slot = num(value["slot"]);
  const tickHz = num(value["tickHz"]) ?? 10;
  const speed = num(value["speedCmPerS"]) ?? DEFAULT_SPEED_CM_PER_S;
  const bounds = value["bounds"] === undefined ? DEFAULT_BOUNDS : parseBounds(value["bounds"]);
  const rawMembers = value["members"];
  if (!isInt(protocolVersion, 0, 1_000) || !isInt(manifestVersion, 0, 1_000_000)) return null;
  if (!isInt(instanceId, 1, 2 ** 31) || !isInt(slot, 0, MAX_AVATARS - 1) || bounds === null) return null;
  if (!(tickHz > 0 && tickHz <= 60) || !(speed > 0 && speed <= 10_000)) return null;
  if (!Array.isArray(rawMembers) || rawMembers.length > MAX_AVATARS) return null;
  const members: Member[] = [];
  const slots = new Set<number>();
  for (const raw of rawMembers) {
    const member = parseMember(raw);
    // One bad member (an unknown preset format) must not hide the whole plaza: skip it.
    if (member === null || slots.has(member.slot)) continue;
    slots.add(member.slot);
    members.push(member);
  }
  return { protocolVersion, manifestVersion, instanceId, slot, tickHz, bounds, speedCmPerS: speed, members };
}

/**
 * Decodes a §5 snapshot: 5 integers per avatar (slot, x cm, z cm, heading 0–255, state). An entry with a
 * value out of range is dropped; a later entry for the same slot wins. Null when the frame is not a snapshot.
 */
export function decodeSnapshot(tickValue: unknown, flat: unknown, bounds: Bounds = DEFAULT_BOUNDS): Snapshot | null {
  const tick = num(tickValue);
  if (!isInt(tick, 0, 0xffff_ffff)) return null;
  if (!(Array.isArray(flat) || ArrayBuffer.isView(flat))) return null;
  const values = flat as ArrayLike<unknown>;
  if (values.length % 5 !== 0 || values.length > MAX_AVATARS * 5) return null;
  const bySlot = new Map<number, AvatarSample>();
  for (let i = 0; i < values.length; i += 5) {
    const slot = num(values[i]);
    const x = num(values[i + 1]);
    const z = num(values[i + 2]);
    const heading = num(values[i + 3]);
    const state = num(values[i + 4]);
    if (!isInt(slot, 0, MAX_AVATARS - 1) || !isInt(heading, 0, 255) || !isInt(state, 0, 2)) continue;
    if (!isInt(x, bounds.minX, bounds.maxX) || !isInt(z, bounds.minZ, bounds.maxZ)) continue;
    bySlot.set(slot, { slot, x, z, heading, state: state as AvatarState });
  }
  return { tick, avatars: [...bySlot.values()] };
}

export function parseSlot(value: unknown): number | null {
  const slot = num(value);
  return isInt(slot, 0, MAX_AVATARS - 1) ? slot : null;
}

export function parseKickReason(value: unknown): KickReason | null {
  return typeof value === "string" && KICK_REASONS.has(value) ? (value as KickReason) : null;
}

export function parseErrorCode(value: unknown): HubErrorCode | null {
  return typeof value === "string" && ERROR_CODES.has(value) ? (value as HubErrorCode) : null;
}

/** The code of a failed `Join` (a HubException message ends with the code), or null for another failure. */
export function joinErrorCode(error: unknown): JoinErrorCode | null {
  const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  return JOIN_ERRORS.find((code) => new RegExp(`(^|\\W)${code}$`).test(message.trim())) ?? null;
}

/** Heading 0–255 to radians: θ = h × 2π / 256; facing (sin θ, cos θ), `rotation.y = θ` for a +z model. */
export function headingToAngle(heading: number): number {
  return (heading * 2 * Math.PI) / 256;
}
