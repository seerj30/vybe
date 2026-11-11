import type { Schemas } from "@vybe/contracts";

export type GameId = Schemas["Ruleset"]["gameId"];

export interface GameInfo {
  id: GameId;
  name: string;
  /** Online route (needs a player); `?challenge=`, `?rematchOf=` and `?to=` work on it. */
  route: string;
  practiceRoute: string;
  blurb: string;
}

/** The games in the arcade, in picker order (spec §16 P1: second mini-game). */
export const GAMES: readonly GameInfo[] = [
  {
    id: "street-hoops",
    name: "Street Hoops",
    route: "/arcade/street-hoops",
    practiceRoute: "/practice",
    blurb: "Aim, charge and release. Hit the moving basket as often as you can in 60 seconds.",
  },
  {
    id: "street-juggle",
    name: "Street Juggle",
    route: "/arcade/street-juggle",
    practiceRoute: "/practice/street-juggle",
    blurb: "Keep the ball up for 60 seconds. Touch the right spot at the right moment; it gets faster.",
  },
];

/** The game of a challenge or session; an unknown id falls back to the first game. */
export function gameInfo(gameId: string): GameInfo {
  return GAMES.find((g) => g.id === gameId) ?? GAMES[0]!;
}
