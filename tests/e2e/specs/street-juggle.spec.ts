import type { Page } from "@playwright/test";
import type { JuggleTouch, StreetJuggleConfig } from "../../../packages/game-rules/src/index.ts";
import { autoplay } from "../../../packages/game-rules/src/juggle-autoplay.ts";
import { expect, newPlayer, summaryValue, walletBalance } from "./app.ts";
import { backdateRunStart } from "./shots.ts";
import { test } from "./street-hoops.ts";

// Street Juggle E2E cases (docs/design/p1-second-game.md §7).
const VERIFIED = "Run verified by VYBE";
const START_PATH = /\/api\/v1\/games\/sessions\/([^/]+)\/start$/;

/**
 * Test data, as `rewriteShots` does for Street Hoops: before the submit leaves the page, the trace becomes the
 * scripted player's run for this seed (`perfect`) or a trace with one tampered value (`tampered`). The server
 * still checks the whole trace.
 */
async function rewriteTouches(page: Page, mode: "perfect" | "tampered"): Promise<void> {
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
async function openOnlineStreetJuggle(page: Page): Promise<void> {
  await page.goto("/arcade/street-juggle");
  await expect(page.getByRole("button", { name: "Start 60 s run" })).toBeEnabled();
}

/** Starts a run; returns the session id. The spot buttons show while the run is on. */
async function startRun(page: Page): Promise<string> {
  const started = page.waitForResponse((r) => START_PATH.test(new URL(r.url()).pathname));
  await page.getByRole("button", { name: /^(Start 60 s run|Play again)$/ }).click();
  const sessionId = START_PATH.exec(new URL((await started).url()).pathname)?.[1] ?? "";
  await expect(page.getByRole("button", { name: "Touch the centre spot" })).toBeVisible();
  return sessionId;
}

/** Touches the spots with the keyboard, each more than minGapMs (150 ms) after the last. */
async function touch(page: Page, count: number): Promise<void> {
  const keys = ["ArrowLeft", "ArrowDown", "ArrowRight"];
  for (let i = 0; i < count; i++) {
    await page.keyboard.press(keys[i % keys.length]!);
    await page.clock.runFor(200);
  }
}

test("S1: guest → hub picker → Street Juggle run → verified score, coins and XP → wallet", async ({ page }) => {
  await newPlayer(page);
  await rewriteTouches(page, "perfect"); // a skilled player: the server pays coins
  await page.clock.install();
  await page.getByRole("link", { name: /Play Street Juggle/ }).click();
  // The overlay is the tutorial.
  await expect(page.getByText(/Keep the ball up\. Touch the spot under the ball/)).toBeVisible();

  const sessionId = await startRun(page);
  // Test data: the server sees a full-length run without 60 s of waiting (the production check is unchanged).
  await backdateRunStart(sessionId, 60_000);
  await touch(page, 5);
  await page.clock.fastForward("01:01");

  await expect(page.getByText(VERIFIED)).toBeVisible();
  expect(await summaryValue(page, "Score")).toBeGreaterThan(6_500);
  const coins = await summaryValue(page, "V-Coins");
  expect(coins).toBe(150);
  expect(await summaryValue(page, "XP")).toBeGreaterThan(0);
  await expect(page.getByRole("link", { name: "Challenge a friend with this run" })).toBeVisible();

  await page.getByRole("link", { name: "Back to the arcade" }).click();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Hub" }).click();
  await expect(walletBalance(page)).toHaveText(String(coins));
});

test("S2: a Street Juggle run with fewer than 5 touches is verified without rewards and says why", async ({ page }) => {
  await newPlayer(page);
  await page.clock.install();
  await openOnlineStreetJuggle(page);
  await startRun(page);
  await touch(page, 1);
  await page.clock.fastForward("01:01");

  await expect(page.getByText(VERIFIED)).toBeVisible();
  await expect(page.getByText(/^No coins: too few touches\. Touch the ball at least 5 times/)).toBeVisible();
  expect(await summaryValue(page, "V-Coins")).toBe(0);
});

test("a tampered Street Juggle trace is refused and pays nothing", async ({ page }) => {
  await newPlayer(page);
  await rewriteTouches(page, "tampered");
  await page.clock.install();
  await openOnlineStreetJuggle(page);
  await startRun(page);
  await touch(page, 3);
  await page.clock.fastForward("01:01");

  await expect(page.getByRole("alert")).toHaveText("VYBE did not count this run: a touch was out of range. No rewards.");
  await page.goto("/hub");
  await expect(walletBalance(page)).toHaveText("0");
});

test("S3: the hub and the arcade offer both games; Street Juggle on the API needs a player", async ({ page }) => {
  await page.goto("/arcade/street-juggle");
  await expect(page).toHaveURL(/\/play$/);

  await newPlayer(page);
  const picker = page.getByRole("list", { name: "Games" });
  await expect(picker.getByRole("link")).toHaveCount(2);
  await expect(picker.getByRole("link", { name: /Play Street Hoops/ })).toBeVisible();
  await expect(picker.getByRole("link", { name: /Play Street Juggle/ })).toHaveAttribute("href", "/arcade/street-juggle");

  await page.goto("/arcade");
  await expect(page.getByRole("heading", { name: "Street Juggle" })).toBeVisible();
  await page.getByRole("link", { name: "Practice Street Juggle (no rewards)" }).click();
  await expect(page).toHaveURL(/\/practice\/street-juggle$/);
  await expect(page.getByRole("button", { name: "Start 60 s run" })).toBeEnabled();
});

test("Street Juggle touches work with the spot buttons and the keyboard", async ({ page }) => {
  await page.clock.install();
  await page.goto("/practice/street-juggle");
  await expect(page.getByRole("button", { name: "Start 60 s run" })).toBeEnabled();
  await page.getByRole("button", { name: "Start 60 s run" }).click();
  // Before the first serve lands, every touch is a whiff: it still shows a result.
  await page.getByRole("button", { name: "Touch the left spot" }).dispatchEvent("pointerdown", { isPrimary: true });
  await expect(page.locator(".result")).toHaveText(/Whiff · streak 0/);
  await page.clock.runFor(600);
  await page.keyboard.press("ArrowRight");
  await expect(page.locator(".result")).toHaveText(/(Perfect|Good|Whiff)( \+\d+)? · streak \d+/);
  await page.clock.fastForward("01:01");
  await expect(page.getByRole("button", { name: "Play again" })).toBeVisible();
  await expect(page.locator(".summary")).toContainText("Kicks");
});
