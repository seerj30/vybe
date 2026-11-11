import { expect, newPlayer, summaryValue, walletBalance } from "./app.ts";
import { backdateRunStart } from "./shots.ts";
import { test } from "./street-hoops.ts";
import { openOnlineStreetJuggle, rewriteTouches, startRun, touch } from "./street-juggle.ts";

// Street Juggle E2E cases (docs/design/p1-second-game.md §7).
const VERIFIED = "Run verified by VYBE";

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

  // Each new level pays 100 V-Coins; the first run's achievement XP (P1, "First Touch") reaches level 2.
  const levelCoins = (await summaryValue(page, "Level") - 1) * 100;

  await page.getByRole("link", { name: "Back to the arcade" }).click();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Hub" }).click();
  await expect(walletBalance(page)).toHaveText(String(coins + levelCoins));
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
