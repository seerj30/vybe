import { describe, expect, it } from "vitest";
import { gameInfo, GAMES } from "./games";

describe("games", () => {
  it("lists both games with distinct routes", () => {
    expect(GAMES.map((g) => g.id)).toEqual(["street-hoops", "street-juggle"]);
    expect(new Set(GAMES.map((g) => g.route)).size).toBe(GAMES.length);
  });

  it("finds a game by id and falls back to Street Hoops", () => {
    expect(gameInfo("street-juggle").route).toBe("/arcade/street-juggle");
    expect(gameInfo("unknown").id).toBe("street-hoops");
  });
});
