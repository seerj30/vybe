import { describe, expect, it } from "vitest";
import { BALL_RADIUS, ballEndPoint, courtPoint, NEAR_Z, FAR_Z, RIM_RADIUS, COURT_HALF_WIDTH } from "./court";

const hoop = courtPoint(200, 600);
const dist = (a: { x: number; z: number }) => Math.hypot(a.x - hoop.x, a.z - hoop.z);

describe("courtPoint", () => {
  it("maps the unit ranges onto the court", () => {
    expect(courtPoint(-1000, 0)).toEqual({ x: -COURT_HALF_WIDTH, z: NEAR_Z });
    expect(courtPoint(1000, 1000)).toEqual({ x: COURT_HALF_WIDTH, z: FAR_Z });
  });
});

describe("ballEndPoint", () => {
  const near = courtPoint(230, 610);
  const far = courtPoint(900, 100);

  it("puts perfect and hit shots through the rim", () => {
    expect(ballEndPoint("perfect", far, hoop)).toEqual(hoop);
    expect(dist(ballEndPoint("hit", near, hoop))).toBeLessThanOrEqual(RIM_RADIUS - BALL_RADIUS);
    expect(dist(ballEndPoint("hit", far, hoop))).toBeLessThanOrEqual(RIM_RADIUS - BALL_RADIUS);
  });

  it("keeps a miss clear of the rim, even when the landing point is inside it", () => {
    expect(dist(ballEndPoint("miss", near, hoop))).toBeGreaterThanOrEqual(RIM_RADIUS + BALL_RADIUS);
    expect(dist(ballEndPoint("miss", hoop, hoop))).toBeGreaterThanOrEqual(RIM_RADIUS + BALL_RADIUS);
    expect(ballEndPoint("miss", far, hoop)).toEqual(far);
  });
});
