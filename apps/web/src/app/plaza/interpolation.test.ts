import { describe, expect, it } from "vitest";
import { Interpolator, lerpAngle, Predictor, SNAP_CM } from "./interpolation";
import { DEFAULT_BOUNDS, type AvatarSample } from "./protocol";

const at = (slot: number, x: number, z: number, heading = 0, state: 0 | 1 | 2 = 1): AvatarSample => ({ slot, x, z, heading, state });

describe("interpolation buffer", () => {
  it("renders 120 ms behind the server time and blends between two snapshots", () => {
    const buffer = new Interpolator(120);
    buffer.push(100, [at(1, 0, 0)], 1000);
    buffer.push(101, [at(1, 100, 0)], 1100);
    // Local 1200 is server tick 102 (10 200 ms); minus 120 ms is 80 % of the way from tick 100 to 101.
    expect(buffer.sample(1, 1200)?.x).toBeCloseTo(80);
    expect(buffer.sample(1, 1100)?.x).toBe(0);
    expect(buffer.sample(1, 1230)?.x).toBeCloseTo(100);
  });

  it("holds the last position when no newer snapshot came (no extrapolation)", () => {
    const buffer = new Interpolator(120);
    buffer.push(10, [at(1, 0, 0)], 0);
    buffer.push(11, [at(1, 35, 0)], 100);
    expect(buffer.sample(1, 900)?.x).toBe(35);
  });

  it("uses the earliest arrival as the clock, so a late frame does not shift the timeline", () => {
    const buffer = new Interpolator(100);
    buffer.push(1, [at(1, 0, 0)], 1000);
    buffer.push(2, [at(1, 10, 0)], 1180); // 80 ms late
    buffer.push(3, [at(1, 20, 0)], 1200);
    // Offset stays 900 (tick 1 at 1000): at 1300 the target is tick 3 exactly.
    expect(buffer.sample(1, 1300)?.x).toBeCloseTo(20);
    expect(buffer.sample(1, 1250)?.x).toBeCloseTo(15);
  });

  it("drops old or repeated ticks", () => {
    const buffer = new Interpolator();
    expect(buffer.push(5, [at(1, 0, 0)], 0)).toBe(true);
    expect(buffer.push(5, [at(1, 9, 0)], 10)).toBe(false);
    expect(buffer.push(4, [at(1, 9, 0)], 10)).toBe(false);
    expect(buffer.latest(1)?.x).toBe(0);
  });

  it("forgets a slot that is not in the next snapshot, and everything on reset", () => {
    const buffer = new Interpolator();
    buffer.push(1, [at(1, 0, 0), at(2, 0, 0)], 0);
    buffer.push(2, [at(1, 0, 0)], 100);
    expect(buffer.has(2)).toBe(false);
    expect(buffer.sample(2, 100)).toBeNull();
    buffer.reset();
    expect(buffer.slots()).toEqual([]);
    expect(buffer.push(1, [at(1, 0, 0)], 200)).toBe(true);
  });

  it("turns the short way around", () => {
    expect(lerpAngle(0.1, 2 * Math.PI - 0.1, 0.5)).toBeCloseTo(0);
    const buffer = new Interpolator(0);
    buffer.push(1, [at(1, 0, 0, 250)], 0);
    buffer.push(2, [at(1, 0, 0, 6)], 100);
    const angle = buffer.sample(1, 50)!.angle;
    expect(Math.abs(Math.atan2(Math.sin(angle), Math.cos(angle)))).toBeLessThan(0.05);
  });

  it("keeps the state of the nearer snapshot", () => {
    const buffer = new Interpolator(0);
    buffer.push(1, [at(1, 0, 0, 0, 1)], 0);
    buffer.push(2, [at(1, 10, 0, 0, 0)], 100);
    expect(buffer.sample(1, 20)?.state).toBe(1);
    expect(buffer.sample(1, 80)?.state).toBe(0);
  });
});

describe("own-avatar prediction", () => {
  it("moves like the server: 350 cm/s at full intent, clamped to the bounds", () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    p.place(0, 1590, 0, 0);
    p.step(0, 127, 100, 100);
    expect(p.z).toBe(1600);
    p.place(0, 0, 0, 0);
    p.step(127, 0, 100, 100);
    expect(p.x).toBeCloseTo(35);
    expect(p.angle).toBeCloseTo(Math.PI / 2);
    p.step(64, 0, 100, 200);
    expect(p.x).toBeCloseTo(35 + 35 * (64 / 127));
  });

  it("stops below the dead zone and keeps the heading", () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    p.place(0, 0, 1, 0);
    p.step(10, 5, 100, 100);
    expect(p.moving).toBe(false);
    expect(p.x).toBe(0);
    expect(p.angle).toBe(1);
  });

  it("does not pull back while the server lags one round trip behind on the same path", () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    p.place(0, 0, 0, 0);
    for (let t = 1; t <= 10; t++) p.step(0, 127, 100, t * 100);
    // Server reports where we were 200 ms ago.
    const error = p.correct(at(0, 0, 280), 1000);
    expect(error).toBeCloseTo(0);
    expect(p.z).toBeCloseTo(350);
  });

  it("pulls a small error back over several snapshots", () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    p.place(0, 0, 0, 0);
    p.correct(at(0, 50, 0, 0, 0), 100);
    expect(p.x).toBeCloseTo(15);
    for (let i = 0; i < 20; i++) p.correct(at(0, 50, 0, 0, 0), 200 + i);
    expect(p.x).toBeCloseTo(50, 0);
  });

  it(`snaps when the error is above ${SNAP_CM} cm`, () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    p.place(0, 0, 0, 0);
    expect(p.correct(at(0, 400, 0, 64, 0), 100)).toBe(400);
    expect(p.x).toBe(400);
    expect(p.angle).toBeCloseTo(Math.PI / 2);
  });

  it("is placed by the first server sample", () => {
    const p = new Predictor(DEFAULT_BOUNDS, 350);
    expect(p.placed).toBe(false);
    p.step(0, 127, 100, 100);
    expect(p.z).toBe(0);
    p.correct(at(0, 120, -30), 100);
    expect(p.placed).toBe(true);
    expect([p.x, p.z]).toEqual([120, -30]);
  });
});
