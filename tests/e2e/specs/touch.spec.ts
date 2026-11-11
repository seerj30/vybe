import type { Page } from "@playwright/test";
import { newPlayer, openOnlineStreetHoops } from "./app.ts";
import { grantCoins } from "./db.ts";
import { buyRoomItem, placedList, roomViewport } from "./room.ts";
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

test("touch: a tap in the 3D room moves the selected item there", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 1_000);
  await buyRoomItem(page, "furn_beanbag");
  await page.goto("/room");
  await expect(roomViewport(page)).toHaveAttribute("data-state", "3d", { timeout: 20_000 });
  await page.getByRole("button", { name: "Place Beanbag" }).click();
  await expect(placedList(page).getByRole("button", { name: /Beanbag: floor, column 1, row 1/ })).toBeVisible();

  // The lower middle of the view is the front half of the floor.
  const box = await roomViewport(page).locator("canvas").boundingBox();
  if (box === null) throw new Error("The room canvas has no layout box");
  await page.touchscreen.tap(box.x + box.width * 0.5, box.y + box.height * 0.72);
  await expect(placedList(page).getByRole("button", { name: /Beanbag: floor, column/ })).not.toHaveAccessibleName(/column 1, row 1/);
  await expect(page.getByText("You have unsaved changes.")).toBeVisible();
});
