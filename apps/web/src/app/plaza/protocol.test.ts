import { describe, expect, it } from "vitest";
import { decodeSnapshot, displayName, headingToAngle, joinErrorCode, MANIFEST_VERSION, parseKickReason, parseLook, parseWelcome, phraseText, PHRASES, STICKERS } from "./protocol";

const look = {
  bodyPresetId: "body_a",
  skinToneId: "skin_02",
  facePresetId: "face_01",
  hairPresetId: "hair_short",
  hairColorId: "black",
  equipped: { hair: null, top: "top_starter_tee", bottom: null, shoes: null, emote: "emote_wave", accessories: ["acc_shades", null] },
};

const welcome = {
  protocolVersion: 1,
  manifestVersion: MANIFEST_VERSION,
  instanceId: 3,
  slot: 2,
  tickHz: 10,
  bounds: { minX: -1600, maxX: 1600, minZ: -1600, maxZ: 1600 },
  speedCmPerS: 350,
  members: [
    { slot: 2, handle: "ana_hoops", look },
    { slot: 5, handle: null, look },
  ],
};

describe("snapshot decoder (protocol §5)", () => {
  it("reads 5 integers per avatar", () => {
    const snapshot = decodeSnapshot(42, [0, 100, -200, 64, 1, 7, -1600, 1600, 255, 2]);
    expect(snapshot).toEqual({
      tick: 42,
      avatars: [
        { slot: 0, x: 100, z: -200, heading: 64, state: 1 },
        { slot: 7, x: -1600, z: 1600, heading: 255, state: 2 },
      ],
    });
  });

  it("accepts typed arrays and bigint ticks (MessagePack uint32/int64)", () => {
    expect(decodeSnapshot(BigInt(7), new Int16Array([3, 1, 2, 3, 0]))?.avatars).toEqual([{ slot: 3, x: 1, z: 2, heading: 3, state: 0 }]);
  });

  it("drops entries out of range and keeps the rest", () => {
    const snapshot = decodeSnapshot(1, [16, 0, 0, 0, 0, 1, 1601, 0, 0, 0, 2, 0, 0, 256, 0, 3, 0, 0, 0, 3, 4, 0.5, 0, 0, 0, 5, 10, 10, 10, 1]);
    expect(snapshot?.avatars.map((a) => a.slot)).toEqual([5]);
  });

  it("lets a later entry of the same slot win", () => {
    expect(decodeSnapshot(1, [1, 0, 0, 0, 0, 1, 50, 50, 0, 1])?.avatars).toEqual([{ slot: 1, x: 50, z: 50, heading: 0, state: 1 }]);
  });

  it("refuses frames that are not snapshots", () => {
    expect(decodeSnapshot(-1, [])).toBeNull();
    expect(decodeSnapshot(1.5, [])).toBeNull();
    expect(decodeSnapshot(1, [0, 0, 0, 0])).toBeNull();
    expect(decodeSnapshot(1, "0,0,0,0,0")).toBeNull();
    expect(decodeSnapshot(1, new Array(17 * 5).fill(0))).toBeNull();
    expect(decodeSnapshot(2 ** 32, [])).toBeNull();
  });
});

describe("welcome and member parsing", () => {
  it("reads a valid welcome", () => {
    const parsed = parseWelcome(welcome);
    expect(parsed?.instanceId).toBe(3);
    expect(parsed?.slot).toBe(2);
    expect(parsed?.members.map((m) => m.handle)).toEqual(["ana_hoops", null]);
    expect(parsed?.members[0]?.look.equipped.accessories).toEqual(["acc_shades", null]);
  });

  it("skips a bad member and a duplicate slot, but keeps the plaza", () => {
    const parsed = parseWelcome({ ...welcome, members: [...welcome.members, { slot: 5, handle: "dup", look }, { slot: 9, handle: "<b>", look }] });
    expect(parsed?.members.map((m) => m.slot)).toEqual([2, 5]);
  });

  it("refuses a welcome without the required fields", () => {
    expect(parseWelcome({ ...welcome, slot: 16 })).toBeNull();
    expect(parseWelcome({ ...welcome, instanceId: 0 })).toBeNull();
    expect(parseWelcome({ ...welcome, members: "x" })).toBeNull();
    expect(parseWelcome(null)).toBeNull();
  });

  it("refuses looks with markup or wrong shapes", () => {
    expect(parseLook(look)).not.toBeNull();
    expect(parseLook({ ...look, skinToneId: "<img>" })).toBeNull();
    expect(parseLook({ ...look, equipped: { ...look.equipped, accessories: ["a", "b", "c"] } })).toBeNull();
    expect(parseLook({ ...look, equipped: { ...look.equipped, top: 5 } })).toBeNull();
  });

  it("names a private member with the generic name", () => {
    expect(displayName({ handle: null })).toBe("Player");
    expect(displayName({ handle: "milo" })).toBe("milo");
  });
});

describe("codes and units", () => {
  it("reads the Join error code from a HubException message", () => {
    expect(joinErrorCode(new Error("An unexpected error occurred invoking 'Join' on the server. HubException: spaces_full"))).toBe("spaces_full");
    expect(joinErrorCode(new Error("rate_limited"))).toBe("rate_limited");
    expect(joinErrorCode(new Error("Server timeout"))).toBeNull();
  });

  it("knows the kick reasons and nothing else", () => {
    expect(parseKickReason("replaced")).toBe("replaced");
    expect(parseKickReason("whatever")).toBeNull();
  });

  it("maps heading 0–255 to radians (64 = a quarter turn)", () => {
    expect(headingToAngle(0)).toBe(0);
    expect(headingToAngle(64)).toBeCloseTo(Math.PI / 2);
  });

  it("bundles the quick-chat manifest (30 phrases, 12 stickers)", () => {
    expect(PHRASES).toHaveLength(30);
    expect(STICKERS).toHaveLength(12);
    expect(phraseText("gg")).toBe("Good game!");
    expect(phraseText("unknown")).toBeUndefined();
  });
});
