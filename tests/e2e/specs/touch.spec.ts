import type { Page } from "@playwright/test";
import { newPlayer, openOnlineStreetHoops } from "./app.ts";
import { expect, expectCharging, openStreetHoops, powerMeter, SHOT_RESULT, startRun, test } from "./street-hoops.ts";

/** Playwright's touchscreen has only tap(); CDP sends a trusted touch that stays down. */
async function touchShot(page: Page): Promise<void> {
  const box = await page.getByLabel(/^Street Hoops court/).boundingBox();
  if (box === null) throw new Error("The court canvas has no layout box");
  const finger = { x: box.x + box.width / 2, y: box.y + box.height * 0.75 };

  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [finger] });
  await expectCharging(page);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });

  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
  await expect(powerMeter(page)).toHaveAttribute("aria-valuenow", "0");
}

test("touch: tap, hold and release on the court records a shot", async ({ page }) => {
  await openStreetHoops(page);
  await startRun(page);
  await touchShot(page);
});

test("touch: onboarding on a phone, then a shot on an API session", async ({ page }) => {
  await newPlayer(page);
  await expect(page.getByRole("navigation", { name: "Main" })).toBeVisible();
  await openOnlineStreetHoops(page);
  await startRun(page);
  await touchShot(page);
});
