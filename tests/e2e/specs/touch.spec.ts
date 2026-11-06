import { expect, test } from "@playwright/test";
import { expectCharging, openStreetHoops, powerMeter, SHOT_RESULT, startRun } from "./street-hoops.ts";

test("touch: tap, hold and release on the court records a shot", async ({ page }) => {
  await openStreetHoops(page);
  await startRun(page);

  const box = await page.getByLabel(/^Street Hoops court/).boundingBox();
  if (box === null) throw new Error("The court canvas has no layout box");
  const finger = { x: box.x + box.width / 2, y: box.y + box.height * 0.75 };

  // Playwright's touchscreen has only tap(); CDP sends a trusted touch that stays down.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [finger] });
  await expectCharging(page);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });

  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
  await expect(powerMeter(page)).toHaveAttribute("aria-valuenow", "0");
});
