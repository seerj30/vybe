/**
 * Real-time spaces hub contract (docs/design/p2-spaces-protocol.md). Hand-written: SignalR messages are not part of
 * the OpenAPI document. The server types are in services/api/src/Vybe.Api/Features/SpaceModel.cs; keep both equal.
 * The ticket endpoint (`POST /spaces/tickets`) is in the OpenAPI document (`Schemas["SpaceTicket"]`).
 */

export const SPACE_HUB_PATH = "/api/v1/spaces/hub";
export const SPACE_PROTOCOL_VERSION = 1;

/** Client → server hub methods (protocol doc §4). */
export const SpaceMethods = {
  join: "Join",
  leave: "Leave",
  move: "Move",
  emote: "Emote",
  quickChat: "QuickChat",
  sticker: "Sticker",
  refreshLook: "RefreshLook",
  /** `Report(slot, key, reason)`: a fixed `SpaceReportReason`, no free text (protocol doc §9). */
  report: "Report",
  /** `Block(slot, key)`: the server then moves the caller (`moved`); in a room, see docs/design/p2-room-visits.md §5. */
  block: "Block",
  /** Room visits (docs/design/p2-room-visits.md §3): open the own room as its owner; returns a room `Welcome`. */
  openRoom: "OpenRoom",
  /** `JoinRoom(handle)`: enter a friend's open room; returns a room `Welcome`. */
  joinRoom: "JoinRoom",
  /** Owner only: close the room (visitors get `kicked("room_closed")`). */
  closeRoom: "CloseRoom",
  /** `RemoveVisitor(slot, key)`, owner only: the visitor gets `kicked("removed")`. */
  removeVisitor: "RemoveVisitor",
} as const;

/** Server → client handlers (protocol doc §5). */
export const SpaceEvents = {
  snapshot: "snapshot",
  joined: "joined",
  left: "left",
  look: "look",
  emote: "emote",
  quickChat: "quickChat",
  sticker: "sticker",
  moved: "moved",
  error: "error",
  kicked: "kicked",
} as const;

/** Fixed rules (protocol doc §3, §4, §6). */
export const SpaceRules = {
  instanceCapacity: 16,
  tickHz: 10,
  boundCm: 1600,
  speedCmPerS: 350,
  maxIntent: 127,
  deadZone: 16,
  moveRepeatMs: 100,
  intentLifetimeMs: 500,
  expressionIntervalMs: 2000,
  lookIntervalMs: 5000,
  reservationMs: 30_000,
  /** Values per avatar in a snapshot: slot, x, z, heading, state. */
  snapshotStride: 5,
  /** Room visits: the owner plus 7 visitors, a 4 m room (30 cm inside the walls), a slower walk. */
  roomCapacity: 8,
  roomBoundCm: 170,
  roomSpeedCmPerS: 250,
} as const;

/** Snapshot `state` values. */
export const AvatarState = { idle: 0, walking: 1, away: 2 } as const;
export type AvatarState = (typeof AvatarState)[keyof typeof AvatarState];

/** `error` codes (non-fatal; the input was dropped). Unknown codes: ignore. */
export type SpaceErrorCode = "rate_limited" | "invalid_input" | "not_joined";

/** `kicked` reasons (protocol doc §8). Unknown reasons: do not reconnect automatically. */
export type SpaceKickReason =
  | "server_shutdown"
  | "replaced"
  | "idle"
  | "not_joined"
  | "rate_limited"
  | "invalid_input"
  | "suspended"
  | "account_unavailable"
  | "spaces_full"
  | "slow_client"
  /** Room visits: the owner closed the room, left, or did not come back within 30 s. */
  | "room_closed"
  /** Room visits: the owner removed the visitor (or blocked them), or the friendship ended. */
  | "removed"
  /** Room visits: the visitor blocked a member, so the visitor left (the blocker moves). */
  | "left_room";

/** `HubException` messages of `Report` and `Block`: the member left (or is the caller), or an error code. */
export type SpaceSafetyError = "not_found" | "rate_limited" | "invalid_input" | "not_joined";

/** Report reasons: the `ReportReason` values of the HTTP API. */
export type SpaceReportReason = "offensive_name" | "cheating" | "harassment" | "other";

/**
 * `HubException` messages of `Join`, `OpenRoom` and `JoinRoom`. `room_unavailable`: not open, not a friend, removed,
 * or a block with a member (one code on purpose).
 */
export type SpaceJoinError =
  | "spaces_full"
  | "already_joined"
  | "rate_limited"
  | "account_unavailable"
  | "room_unavailable"
  | "room_full"
  | "invalid_input";

export interface SpaceEquipped {
  hair: string | null;
  top: string | null;
  bottom: string | null;
  shoes: string | null;
  emote: string | null;
  accessories: (string | null)[];
}

/** The `GET /profiles` avatar shape, with item SKUs instead of item ids. */
export interface SpaceLook {
  bodyPresetId: string;
  skinToneId: string;
  facePresetId: string;
  hairPresetId: string;
  hairColorId: string;
  equipped: SpaceEquipped;
}

export interface SpaceMember {
  slot: number;
  /** New for each placement in the instance (slots are reused); `Report` and `Block` send slot and key. Not a player id. */
  key: number;
  /** null for a private profile (decision 6): show a generic name. */
  handle: string | null;
  look: SpaceLook;
}

export type SpaceKind = "plaza" | "room";

export type SpaceRoomSurface = "floor" | "wall_back" | "wall_left";

/** One placed item of an open room: SKUs only (no item ids). */
export interface SpaceRoomItem {
  sku: string;
  surface: SpaceRoomSurface;
  x: number;
  y: number;
  rotation: 0 | 90 | 180 | 270;
}

/** The owner's saved room layout at open time: finish SKUs and placed items. */
export interface SpaceRoom {
  wall: string | null;
  floor: string | null;
  items: SpaceRoomItem[];
}

export interface SpaceWelcome {
  protocolVersion: number;
  manifestVersion: number;
  instanceId: number;
  slot: number;
  tickHz: number;
  bounds: { minX: number; maxX: number; minZ: number; maxZ: number };
  speedCmPerS: number;
  members: SpaceMember[];
  /** Optional room fields (docs/design/p2-room-visits.md §3): missing or "plaza" in the plaza. */
  space?: SpaceKind;
  /** Room: the owner's slot. */
  ownerSlot?: number | null;
  /** Room: the owner's saved layout. */
  room?: SpaceRoom | null;
}

export interface SpaceAvatarSnapshot {
  slot: number;
  x: number;
  z: number;
  heading: number;
  state: AvatarState;
}

/** Splits the flat snapshot array (5 integers per avatar) into entries. */
export function readSnapshot(avatars: readonly number[]): SpaceAvatarSnapshot[] {
  const entries: SpaceAvatarSnapshot[] = [];
  for (let i = 0; i + SpaceRules.snapshotStride <= avatars.length; i += SpaceRules.snapshotStride) {
    entries.push({ slot: avatars[i]!, x: avatars[i + 1]!, z: avatars[i + 2]!, heading: avatars[i + 3]!, state: avatars[i + 4] as AvatarState });
  }
  return entries;
}

/** Heading (0–255) to radians: `object.rotation.y` for a model that faces +z. */
export function headingToRadians(heading: number): number {
  return (heading * 2 * Math.PI) / 256;
}
