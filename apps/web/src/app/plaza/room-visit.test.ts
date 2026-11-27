import { describe, expect, it } from "vitest";
import { fromSpaceRoom } from "../room/room-layout";
import { forView } from "./input";
import { type HubLike, joinCall, PlazaClient, type PlazaEvents, type PlazaStatus, type SpaceTarget } from "./plaza-client";
import { joinErrorCode, MANIFEST_VERSION, parseKickReason, parseWelcome, type Welcome } from "./protocol";
import { KICK_RULES } from "./reconnect";
import { statusView } from "./status-view";

/** Live friend room visits, client side (docs/design/p2-room-visits.md). */

const look = {
  bodyPresetId: "body_a",
  skinToneId: "skin_02",
  facePresetId: "face_01",
  hairPresetId: "hair_short",
  hairColorId: "black",
  equipped: { hair: null, top: null, bottom: null, shoes: null, emote: null, accessories: [] },
};
const ROOM_WELCOME = {
  protocolVersion: 1,
  manifestVersion: MANIFEST_VERSION,
  instanceId: 4,
  slot: 1,
  tickHz: 10,
  bounds: { minX: -170, maxX: 170, minZ: -170, maxZ: 170 },
  speedCmPerS: 250,
  members: [
    { slot: 0, key: 1, handle: null, look },
    { slot: 1, key: 2, handle: "visitor", look },
  ],
  space: "room",
  ownerSlot: 0,
  room: {
    wall: "walls_brick",
    floor: null,
    items: [
      { sku: "furn_floor_lamp", surface: "floor", x: 2, y: 5, rotation: 90 },
      { sku: "bad sku!", surface: "floor", x: 0, y: 0, rotation: 0 },
      { sku: "decor_hoops_poster", surface: "ceiling", x: 0, y: 0, rotation: 0 },
      { sku: "furn_round_rug", surface: "floor", x: 1, y: 1, rotation: 45 },
    ],
  },
};

describe("room Welcome (design §3)", () => {
  it("reads the room fields and skips bad layout items", () => {
    const welcome = parseWelcome(ROOM_WELCOME);
    expect(welcome).toMatchObject({ space: "room", ownerSlot: 0, slot: 1, speedCmPerS: 250 });
    expect(welcome?.room).toEqual({ wall: "walls_brick", floor: null, items: [{ sku: "furn_floor_lamp", surface: "floor", x: 2, y: 5, rotation: 90 }] });
  });

  it("a plaza Welcome without the new fields is a plaza", () => {
    const { space: _space, ownerSlot: _owner, room: _room, ...plaza } = ROOM_WELCOME;
    expect(parseWelcome({ ...plaza, bounds: { minX: -1600, maxX: 1600, minZ: -1600, maxZ: 1600 } })).toMatchObject({ space: "plaza", ownerSlot: null, room: null });
  });

  it("refuses a room without a layout, a slot past 8 and an unknown space", () => {
    expect(parseWelcome({ ...ROOM_WELCOME, room: null })).toBeNull();
    expect(parseWelcome({ ...ROOM_WELCOME, room: { wall: null, floor: null, items: "x" } })).toBeNull();
    expect(parseWelcome({ ...ROOM_WELCOME, ownerSlot: 9 })).toBeNull();
    expect(parseWelcome({ ...ROOM_WELCOME, space: "garden" })).toBeNull();
    expect(
      parseWelcome({ ...ROOM_WELCOME, slot: 9, members: [...ROOM_WELCOME.members, { slot: 9, key: 3, handle: "x", look }] }),
    ).toBeNull();
  });

  it("knows the room kick reasons and join errors", () => {
    for (const reason of ["room_closed", "removed", "left_room"] as const) {
      expect(parseKickReason(reason)).toBe(reason);
      expect(KICK_RULES[reason].action.kind).toBe("none");
    }
    expect(joinErrorCode(new Error("HubException: room_unavailable"))).toBe("room_unavailable");
    expect(joinErrorCode(new Error("HubException: room_full"))).toBe("room_full");
    expect(joinErrorCode(new Error("HubException: account_unavailable"))).toBe("account_unavailable");
  });

  it("turns the layout into a room to show, with local item ids", () => {
    const room = fromSpaceRoom(parseWelcome(ROOM_WELCOME)!.room!);
    expect(room).toEqual({
      wallItem: { itemId: "wall", sku: "walls_brick" },
      floorItem: null,
      items: [{ itemId: "item-0", sku: "furn_floor_lamp", surface: "floor", x: 2, y: 5, rotation: 90 }],
    });
  });
});

/** A hub that answers every invoke with `answer` and records the calls. */
class RecordingHub implements HubLike {
  readonly handlers = new Map<string, (...args: unknown[]) => void>();
  readonly invoked: unknown[][] = [];
  readonly sent: unknown[][] = [];
  stopped = false;
  constructor(private readonly answer: () => Promise<unknown>) {}
  start(): Promise<void> {
    return Promise.resolve();
  }
  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }
  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invoked.push([method, ...args]);
    return this.invoked.length === 1 ? this.answer() : Promise.resolve(null);
  }
  send(method: string, ...args: unknown[]): Promise<void> {
    this.sent.push([method, ...args]);
    return Promise.resolve();
  }
  on(name: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(name, handler);
  }
  onclose(): void {}
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function setup(target: SpaceTarget, answer: () => Promise<unknown> = () => Promise.resolve(ROOM_WELCOME)) {
  const hubs: RecordingHub[] = [];
  const statuses: PlazaStatus[] = [];
  const welcomes: Welcome[] = [];
  const noop = () => undefined;
  const events: PlazaEvents = {
    status: (s) => statuses.push(s),
    welcome: (w) => welcomes.push(w),
    snapshot: noop,
    joined: noop,
    left: noop,
    look: noop,
    emote: noop,
    quickChat: noop,
    sticker: noop,
    error: noop,
  };
  const client = new PlazaClient(
    () => {
      const hub = new RecordingHub(answer);
      hubs.push(hub);
      return hub;
    },
    events,
    undefined,
    () => null,
    target,
  );
  return { client, hubs, statuses, welcomes };
}

describe("room client", () => {
  it("enters by OpenRoom or JoinRoom(handle)", async () => {
    expect(joinCall({ kind: "plaza" })).toEqual(["Join"]);
    expect(joinCall({ kind: "host" })).toEqual(["OpenRoom"]);
    expect(joinCall({ kind: "visit", handle: "ana_hoops" })).toEqual(["JoinRoom", "ana_hoops"]);

    const { client, hubs, welcomes } = setup({ kind: "visit", handle: "ana_hoops" });
    client.connect();
    await flush();
    expect(hubs[0]!.invoked[0]).toEqual(["JoinRoom", "ana_hoops"]);
    expect(welcomes[0]?.space).toBe("room");
  });

  it("the owner removes by slot and key, and closes the room with CloseRoom (not Leave)", async () => {
    const { client, hubs, statuses } = setup({ kind: "host" });
    client.connect();
    await flush();
    await client.removeVisitor(1, 2);
    expect(hubs[0]!.invoked).toEqual([["OpenRoom"], ["RemoveVisitor", 1, 2]]);
    client.closeRoom();
    await flush();
    expect(hubs[0]!.sent).toEqual([["CloseRoom"]]);
    expect(hubs[0]!.stopped).toBe(true);
    expect(statuses.at(-1)?.kind).toBe("closed");
  });

  it("room_unavailable stops without a retry; room_full offers one", async () => {
    const unavailable = setup({ kind: "visit", handle: "x_y" }, () => Promise.reject(new Error("HubException: room_unavailable")));
    unavailable.client.connect();
    await flush();
    expect(unavailable.statuses.at(-1)).toMatchObject({ kind: "failed", retry: false });
    expect(unavailable.hubs).toHaveLength(1);

    const full = setup({ kind: "visit", handle: "x_y" }, () => Promise.reject(new Error("HubException: room_full")));
    full.client.connect();
    await flush();
    expect(full.statuses.at(-1)).toMatchObject({ kind: "failed", retry: true });
  });

  it("a room kick never reconnects by itself", async () => {
    const { client, hubs, statuses } = setup({ kind: "visit", handle: "x_y" });
    client.connect();
    await flush();
    hubs[0]!.handlers.get("kicked")?.("room_closed");
    await flush();
    expect(statuses.at(-1)).toMatchObject({ kind: "kicked", reason: "room_closed" });
    expect(hubs).toHaveLength(1);
  });
});

describe("room overlay and input", () => {
  it("names the room and offers a way out after a room end", () => {
    const closed = statusView({ kind: "kicked", reason: "room_closed", rule: KICK_RULES.room_closed, retryAt: null }, "room");
    expect(closed).toMatchObject({ title: "The room closed", text: "The room closed.", exits: true, action: null });
    const failed = statusView({ kind: "failed", message: "This room is not open for you now.", retry: false, leave: false }, "room");
    expect(failed).toMatchObject({ title: "The room is not available", exits: true });
    expect(statusView({ kind: "connecting" }, "room")).toMatchObject({ text: "Entering the room…", exits: false });
    // The plaza overlay does not change.
    expect(statusView({ kind: "connecting" })).toEqual({ title: "Entering the plaza", text: "Entering the plaza…", busy: true, action: null, button: "", exits: false });
  });

  it("turns screen input 180° for the room camera", () => {
    expect(forView({ dx: -127, dz: 0 }, "room")).toEqual({ dx: 127, dz: 0 });
    expect(forView({ dx: 0, dz: 127 }, "room")).toEqual({ dx: 0, dz: -127 });
    expect(Object.is(forView({ dx: 0, dz: 0 }, "room").dx, 0)).toBe(true);
    expect(forView({ dx: 5, dz: 90 }, "plaza")).toEqual({ dx: 5, dz: 90 });
  });
});
