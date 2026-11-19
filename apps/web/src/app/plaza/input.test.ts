import { describe, expect, it } from "vitest";
import { directionOf, intentFromJoystick, intentFromKeys, MoveSender, STOP } from "./input";

function sender() {
  const sent: [number, number, number][] = [];
  let now = 0;
  const s = new MoveSender((dx, dz) => sent.push([now, dx, dz]));
  return {
    s,
    sent,
    at(time: number) {
      now = time;
      return time;
    },
  };
}

describe("intents", () => {
  it("maps keys to plaza axes (forward +z, screen right −x)", () => {
    expect(intentFromKeys(new Set(["forward"]))).toEqual({ dx: 0, dz: 127 });
    expect(intentFromKeys(new Set(["right"]))).toEqual({ dx: -127, dz: 0 });
    expect(intentFromKeys(new Set(["left", "back"]))).toEqual({ dx: 90, dz: -90 });
    expect(intentFromKeys(new Set(["left", "right"]))).toEqual(STOP);
    expect(directionOf("KeyW")).toBe("forward");
    expect(directionOf("ArrowLeft")).toBe("left");
    expect(directionOf("KeyQ")).toBeUndefined();
  });

  it("scales the joystick by distance, with a dead zone in the middle", () => {
    expect(intentFromJoystick(0, -44, 44)).toEqual({ dx: 0, dz: 127 });
    expect(intentFromJoystick(0, -22, 44)).toEqual({ dx: 0, dz: 64 });
    expect(intentFromJoystick(3, 2, 44)).toEqual(STOP);
    expect(intentFromJoystick(0, 200, 44)).toEqual({ dx: 0, dz: -127 });
  });

  it("never sends −0", () => {
    expect(Object.is(intentFromKeys(new Set(["forward"])).dx, -0)).toBe(false);
  });
});

describe("Move repeat (protocol §4)", () => {
  it("sends at once, repeats every 100 ms while moving, and sends one stop", () => {
    const { s, sent, at } = sender();
    s.set({ dx: 0, dz: 127 }, at(0));
    for (let t = 25; t <= 250; t += 25) s.tick(at(t));
    s.set(STOP, at(260));
    for (let t = 275; t <= 600; t += 25) s.tick(at(t));
    expect(sent).toEqual([
      [0, 0, 127],
      [100, 0, 127],
      [200, 0, 127],
      [300, 0, 0],
    ]);
  });

  it("never sends more than one Move per 100 ms, and the newest intent wins", () => {
    const { s, sent, at } = sender();
    s.set({ dx: 0, dz: 127 }, at(0));
    s.set({ dx: 20, dz: 120 }, at(10));
    s.set({ dx: 40, dz: 110 }, at(40));
    s.tick(at(90));
    s.tick(at(100));
    expect(sent).toEqual([
      [0, 0, 127],
      [100, 40, 110],
    ]);
  });

  it("sends nothing while standing still", () => {
    const { s, sent, at } = sender();
    for (let t = 0; t < 1000; t += 25) s.tick(at(t));
    s.set(STOP, at(1000));
    expect(sent).toEqual([]);
  });

  it("on a hidden tab sends Move(0, 0) at once and then no input until enabled", () => {
    const { s, sent, at } = sender();
    s.set({ dx: 127, dz: 0 }, at(0));
    s.disable(at(30));
    s.set({ dx: 127, dz: 0 }, at(200));
    for (let t = 200; t < 600; t += 25) s.tick(at(t));
    expect(sent).toEqual([
      [0, 127, 0],
      [30, 0, 0],
    ]);
    expect(s.current).toEqual(STOP);
    s.enable();
    s.set({ dx: 127, dz: 0 }, at(700));
    expect(sent.at(-1)).toEqual([700, 127, 0]);
  });

  it("does not send a stop when it was not moving", () => {
    const { s, sent, at } = sender();
    s.disable(at(0));
    expect(sent).toEqual([]);
  });
});
