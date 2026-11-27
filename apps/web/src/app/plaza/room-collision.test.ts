import { describe, expect, it } from "vitest";
import vectors from "../../../../../packages/contracts/fixtures/room-collision.v1.json";
import { Predictor } from "./interpolation";
import type { Bounds, RoomLayout } from "./protocol";
import { AVATAR_RADIUS_CM, moveAround, roomObstacles } from "./room-collision";

const ROOM_BOUNDS: Bounds = { minX: -vectors.boundCm, maxX: vectors.boundCm, minZ: -vectors.boundCm, maxZ: vectors.boundCm };
const layoutOf = (items: (typeof vectors.cases)[number]["items"]): RoomLayout => ({
  wall: null,
  floor: null,
  items: items as RoomLayout["items"],
});

describe("furniture collision: the shared vectors (same as the API's RoomCollisionTests)", () => {
  it("uses the avatar radius of the API", () => {
    expect(AVATAR_RADIUS_CM).toBe(vectors.avatarRadiusCm);
  });

  for (const vector of vectors.cases) {
    it(vector.name, () => {
      const obstacles = roomObstacles(layoutOf(vector.items));
      expect(obstacles.map((o) => [o.minX, o.minZ, o.maxX, o.maxZ])).toEqual(vector.obstacles);
      for (const walk of vector.walks) {
        // The predictor at the server tick (100 ms) walks where the server walks.
        const predictor = new Predictor(ROOM_BOUNDS, vectors.speedCmPerS, obstacles);
        predictor.place(walk.from[0]!, walk.from[1]!, 0, 0);
        for (let tick = 1; tick <= walk.ticks; tick++) predictor.step(walk.intent[0]!, walk.intent[1]!, 100, tick * 100);
        expect(predictor.x).toBeCloseTo(walk.to[0]!, 2);
        expect(predictor.z).toBeCloseTo(walk.to[1]!, 2);
      }
    });
  }
});

describe("furniture collision in the predictor", () => {
  const lamp = roomObstacles(layoutOf([{ sku: "furn_floor_lamp", surface: "floor", x: 4, y: 4, rotation: 0 }]));

  it("does not let a correction pull the avatar into furniture", () => {
    const predictor = new Predictor(ROOM_BOUNDS, 250, lamp);
    predictor.place(-40, 25, 0, 0);
    // The server sample is 60 cm away, behind the lamp's edge at x = −20: the pull stops at the edge.
    predictor.correct({ slot: 0, x: 20, z: 25, heading: 0, state: 0 }, 50, 1);
    expect(predictor.x).toBe(-20);
  });

  it("is a plain clamp with no obstacles (the plaza)", () => {
    expect(moveAround(160, -160, 30, -30, [], ROOM_BOUNDS)).toEqual([170, -170]);
  });
});
