import type { APIResponse } from "@playwright/test";
import { expect, newPlayer, openOnlineStreetHoops, RUN_TIMEOUT, shoot, summaryValue, walletBalance } from "./app.ts";
import { backdateRunStart, rewriteShots } from "./shots.ts";
import { startRun, test } from "./street-hoops.ts";

const VERIFIED = "Run verified by VYBE";

interface SentSubmit {
  url: string;
  headers: Record<string, string>;
  body: string;
}

async function ledgerReasons(response: APIResponse): Promise<string[]> {
  expect(response.status()).toBe(200);
  const page = (await response.json()) as { items: { reason: string }[] };
  return page.items.map((entry) => entry.reason);
}

test("E01: guest → avatar → tutorial → verified game → wallet", async ({ page }) => {
  test.setTimeout(150_000);
  await newPlayer(page);
  await rewriteShots(page, "aimed"); // a skilled player: score ≥ 500, so the run earns coins
  await page.getByRole("link", { name: "Play Street Hoops" }).click();
  // The overlay is the tutorial: it explains aim and shoot before the first run.
  await expect(page.getByText(/Aim: move the pointer or finger/)).toBeVisible();

  await startRun(page);
  await shoot(page, 5);
  await expect(page.getByText(VERIFIED)).toBeVisible(RUN_TIMEOUT);
  const coins = await summaryValue(page, "V-Coins");
  expect(coins).toBeGreaterThan(0);
  expect(await summaryValue(page, "XP")).toBeGreaterThan(0);
  // Each new level pays 100 V-Coins (achievement XP can reach level 2 on the first run, P1).
  const levelCoins = (await summaryValue(page, "Level") - 1) * 100;

  await page.getByRole("link", { name: "Back to the arcade" }).click();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Hub" }).click();
  await expect(walletBalance(page)).toHaveText(String(coins + levelCoins));
});

test("E05: a lost submit response, retried, and duplicate submits pay the reward once", async ({ page }) => {
  test.setTimeout(150_000);
  await newPlayer(page);

  // Assigned in the route handler; TypeScript cannot see that, so the type is widened by hand.
  let first = null as SentSubmit | null;
  await page.route("**/api/v1/games/sessions/*/submit", async (route) => {
    if (first !== null) return route.continue();
    const request = route.request();
    first = { url: request.url(), headers: await request.allHeaders(), body: request.postData() ?? "" };
    // The server verifies the run and pays, but the answer never reaches the client.
    const response = await route.fetch();
    expect(response.status()).toBe(200);
    await route.abort("connectionreset");
  });
  await rewriteShots(page, "aimed"); // runs before the handler above; score ≥ 500, so the run earns coins

  await openOnlineStreetHoops(page);
  await startRun(page);
  await shoot(page, 5);
  await expect(page.getByRole("alert")).toHaveText(/No connection to VYBE/, RUN_TIMEOUT);
  await page.getByRole("button", { name: "Retry sending the run" }).click();
  await expect(page.getByText(VERIFIED)).toBeVisible();
  const score = await summaryValue(page, "Score");
  const coins = await summaryValue(page, "V-Coins");
  const levelCoins = (await summaryValue(page, "Level") - 1) * 100; // achievement XP can add a level (P1)

  // The same request again, with its key and with a new key: the canonical result, no second reward.
  if (first === null) throw new Error("The submit request was not seen");
  const sent: SentSubmit = first;
  const headers = {
    "content-type": "application/json",
    "x-requested-with": "vybe-web",
    "idempotency-key": sent.headers["idempotency-key"] ?? "",
  };
  for (const key of [headers["idempotency-key"], crypto.randomUUID()]) {
    const replay = await page.request.post(sent.url, { headers: { ...headers, "idempotency-key": key }, data: sent.body });
    expect(replay.status()).toBe(200);
    expect(await replay.json()).toMatchObject({ status: "verified", score });
  }

  const reasons = await ledgerReasons(await page.request.get("/api/v1/me/wallet/transactions"));
  expect(reasons.filter((r) => r === "game_reward")).toHaveLength(1);
  await page.goto("/hub");
  await expect(walletBalance(page)).toHaveText(String(coins + levelCoins));
});

test("a trace that claims more game time than has passed is rejected and pays nothing", async ({ page }) => {
  await newPlayer(page);
  // Only the page clock jumps: the shot claims tMs ≈ 30 000 two seconds after the server started the run,
  // so the server answers submitted_too_early (spec §7 check 9).
  await page.clock.install();
  await openOnlineStreetHoops(page);
  await startRun(page);
  await page.clock.fastForward("00:30");
  await shoot(page, 1);
  await page.clock.fastForward("00:31");

  await expect(page.getByRole("alert")).toHaveText("VYBE did not count this run: it claimed more game time than had passed. No rewards.");
  await expect(page.getByText(VERIFIED)).toHaveCount(0);
  await page.goto("/hub");
  await expect(walletBalance(page)).toHaveText("0");
});

test("a run with too few shots is verified without rewards and says why", async ({ page }) => {
  await newPlayer(page);
  await page.clock.install();
  await openOnlineStreetHoops(page);
  await startRun(page);
  await shoot(page, 1);
  await page.clock.fastForward("01:01");

  await expect(page.getByText(VERIFIED)).toBeVisible();
  await expect(page.getByText(/^No coins: too few shots\. Take at least 5 shots/)).toBeVisible();
  expect(await summaryValue(page, "V-Coins")).toBe(0);
});

test("a run that ends before 58 s of real time is verified without rewards and says why", async ({ page }) => {
  await newPlayer(page);
  await rewriteShots(page, "aimed"); // a high score does not help: the run is too short
  // Only the page clock jumps: the run ends on the client after a few seconds of real time.
  await page.clock.install();
  await openOnlineStreetHoops(page);
  await startRun(page);
  await shoot(page, 5);
  await page.clock.fastForward("01:01");

  await expect(page.getByText(VERIFIED)).toBeVisible();
  await expect(page.getByText("No coins: run too short. Play the full run to earn V-Coins, XP and quest progress.")).toBeVisible();
  expect(await summaryValue(page, "V-Coins")).toBe(0);
  expect(await summaryValue(page, "XP")).toBe(0);
  await expect(page.getByRole("link", { name: "Challenge a friend with this run" })).toHaveCount(0);
});

test("a full-length run below 500 points earns XP but no coins and says why", async ({ page }) => {
  await newPlayer(page);
  await rewriteShots(page, "missed");
  await page.clock.install();
  await openOnlineStreetHoops(page);
  const started = page.waitForResponse((r) => /\/api\/v1\/games\/sessions\/[^/]+\/start$/.test(new URL(r.url()).pathname));
  await startRun(page);
  const sessionId = /sessions\/([^/]+)\/start$/.exec((await started).url())?.[1] ?? "";
  // Test data: the server sees a full-length run without 60 s of waiting (the production check is unchanged).
  await backdateRunStart(sessionId, 60_000);
  await shoot(page, 5);
  await page.clock.fastForward("01:01");

  await expect(page.getByText(VERIFIED)).toBeVisible();
  await expect(page.getByText("No coins: score below 500. Your XP and quest progress still count.")).toBeVisible();
  expect(await summaryValue(page, "Score")).toBe(0);
  expect(await summaryValue(page, "V-Coins")).toBe(0);
  expect(await summaryValue(page, "XP")).toBeGreaterThan(0);
  // A reward-eligible run can start a challenge, whatever its coin tier.
  await expect(page.getByRole("link", { name: "Challenge a friend with this run" })).toBeVisible();
});

test("a failed session start shows the reason, and Start works again", async ({ page }) => {
  await newPlayer(page);
  let failed = false;
  await page.route("**/api/v1/games/street-hoops/sessions", async (route) => {
    if (failed) return route.continue();
    failed = true;
    await route.abort("connectionreset");
  });

  await openOnlineStreetHoops(page);
  await page.getByRole("button", { name: "Start 60 s run" }).click();
  await expect(page.getByRole("alert")).toHaveText(/No connection to VYBE/);
  await startRun(page);
});

test("an online run has no tuning panel: the server's ruleset and seed apply", async ({ page }) => {
  await newPlayer(page);
  await openOnlineStreetHoops(page);
  await expect(page.getByText("Tuning (dev)")).toHaveCount(0);
});

test("Street Hoops on the API needs a player", async ({ page }) => {
  await page.goto("/arcade/street-hoops");
  await expect(page).toHaveURL(/\/play$/);
});
