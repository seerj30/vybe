import type { Page } from "@playwright/test";
import type { JuggleTouch, StreetJuggleConfig } from "../../../packages/game-rules/src/index.ts";
import { autoplay } from "../../../packages/game-rules/src/juggle-autoplay.ts";
import { expect } from "./app.ts";

// Street Juggle E2E helpers (docs/design/p1-second-game.md §7), shared by the specs.
export const START_PATH = /\/api\/v1\/games\/sessions\/([^/]+)\/start$/;

/**
 * Test data, as `rewriteShots` does for Street Hoops: before the submit leaves the page, the trace becomes the
 * scripted player's run for this seed (`perfect`) or a trace with one tampered value (`tampered`). The server
 * still checks the whole trace.
 */
export async function rewriteTouches(page: Page, mode: "perfect" | "tampered"): Promise<void> {
  let seed: number | undefined;
  page.on("response", async (response) => {
    if (START_PATH.test(new URL(response.url()).pathname) && response.ok()) {
      seed = ((await response.json()) as { seed: number }).seed;
    }
  });
  await page.route("**/api/v1/games/sessions/*/submit", async (route) => {
    const body = JSON.parse(route.request().postData() ?? "{}") as { rulesetId: string; events: JuggleTouch[] };
    if (seed === undefined) throw new Error("The start response was not seen");
    const ruleset = await page.request.get(`/api/v1/games/rulesets/${body.rulesetId}`);
    const { config } = (await ruleset.json()) as { config: StreetJuggleConfig };
    const events = mode === "perfect" ? autoplay(config, seed) : body.events.map((e, i) => (i === 0 ? { ...e, lane: config.lanes } : e));
    await route.fallback({ postData: JSON.stringify({ ...body, events }) });
  });
}

/** Opens Street Juggle on an API session (needs a player). */
export async function openOnlineStreetJuggle(page: Page): Promise<void> {
  await page.goto("/arcade/street-juggle");
  await expect(page.getByRole("button", { name: "Start 60 s run" })).toBeEnabled();
}

/** Starts a run; returns the session id. The spot buttons show while the run is on. */
export async function startRun(page: Page): Promise<string> {
  const started = page.waitForResponse((r) => START_PATH.test(new URL(r.url()).pathname));
  await page.getByRole("button", { name: /^(Start 60 s run|Play again)$/ }).click();
  const sessionId = START_PATH.exec(new URL((await started).url()).pathname)?.[1] ?? "";
  await expect(page.getByRole("button", { name: "Touch the centre spot" })).toBeVisible();
  return sessionId;
}

/** Touches the spots with the keyboard, each more than minGapMs (150 ms) after the last. */
export async function touch(page: Page, count: number): Promise<void> {
  const keys = ["ArrowLeft", "ArrowDown", "ArrowRight"];
  for (let i = 0; i < count; i++) {
    await page.keyboard.press(keys[i % keys.length]!);
    await page.clock.runFor(200);
  }
}
