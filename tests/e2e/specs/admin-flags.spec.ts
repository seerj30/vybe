import type { APIRequestContext } from "@playwright/test";
import { createUser } from "../fake-auth/users.ts";
import { guestPost, json, signInOnAdmin } from "./account-flows.ts";
import { ADULT_GUEST, expect } from "./app.ts";
import { backdateRunStart } from "./shots.ts";
import { test } from "./street-hoops.ts";

const UNSAFE = { "content-type": "application/json", "x-requested-with": "vybe-web" };

/**
 * Test data: a verified run that the server flags `gap_variance_low` (spec §17 decision 4): 25 shots exactly
 * `minGapMs` apart. The run start is moved back, so the run is full length without 60 s of waiting.
 */
async function flaggedRun(request: APIRequestContext): Promise<string> {
  const created = await request.post("/api/v1/games/street-hoops/sessions", { headers: UNSAFE, data: {} });
  expect(created.status()).toBe(201);
  const { sessionId, rulesetId } = (await created.json()) as { sessionId: string; rulesetId: string };
  const { config } = (await (await request.get(`/api/v1/games/rulesets/${rulesetId}`)).json()) as {
    config: { minGapMs: number; minRunMsForRewards: number };
  };
  const events = Array.from({ length: 25 }, (_, i) => ({ tMs: 500 + i * config.minGapMs, aimX: 0, power: 600 }));
  expect((await request.post(`/api/v1/games/sessions/${sessionId}/start`, { headers: UNSAFE })).status()).toBe(200);
  await backdateRunStart(sessionId, config.minRunMsForRewards);
  const submitted = await request.post(`/api/v1/games/sessions/${sessionId}/submit`, {
    headers: { ...UNSAFE, "idempotency-key": crypto.randomUUID() },
    data: { rulesetId, events },
  });
  expect(submitted.status()).toBe(200);
  expect(await submitted.json()).toMatchObject({ status: "verified" });
  return sessionId;
}

test("moderator: sees flagged runs with player, flags, score and run time; the filter narrows the list", async ({ page, browser }) => {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const guest = await json<{ playerId: string; handle: string }>(await guestPost(context.request, "/guests", ADULT_GUEST));
  await flaggedRun(context.request);

  await signInOnAdmin(page, await createUser({ role: "moderator" }));
  await expect(page.getByRole("heading", { name: "Review flags" })).toBeVisible();
  await page.getByRole("link", { name: "Review flags" }).click();
  await expect(page.locator("#flags")).toBeFocused();

  const row = page.getByRole("row", { name: `Flagged run of ${guest.handle}` });
  await expect(row).toContainText("Even shot timing");
  await expect(row).toContainText(guest.playerId);
  await expect(row).toContainText("25 shots");
  await expect(row).toContainText(/\d+\.\d s/);

  await page.getByLabel("Flag filter").selectOption("daily_cap_streak");
  await expect(row).toHaveCount(0);
  await page.getByLabel("Flag filter").selectOption("gap_variance_low");
  await expect(row).toBeVisible();
  await context.close();
});
