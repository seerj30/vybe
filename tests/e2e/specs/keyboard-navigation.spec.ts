import type { Locator } from "@playwright/test";
import { expect, openStreetHoops, powerMeter, SHOT_RESULT, startButton, test } from "./street-hoops.ts";

async function expectFocusVisible(locator: Locator): Promise<void> {
  await expect(locator).toBeFocused();
  expect(await locator.evaluate((element) => element.matches(":focus-visible"))).toBe(true);
}

test("keyboard only: Tab through the controls, toggle Reduce motion, start a run and shoot", async ({ page }) => {
  await openStreetHoops(page);
  const court = page.getByLabel(/^Street Hoops court/);
  const reduceMotion = page.getByRole("checkbox", { name: "Reduce motion" });
  const initiallyReduced = await reduceMotion.isChecked();

  // Tab order follows the screen: court, Reduce motion, Start.
  await page.keyboard.press("Tab");
  await expectFocusVisible(court);
  await page.keyboard.press("Tab");
  await expectFocusVisible(reduceMotion);
  await page.keyboard.press("Space");
  await expect(reduceMotion).toBeChecked({ checked: !initiallyReduced });
  await page.keyboard.press("Tab");
  await expectFocusVisible(startButton(page));

  // Enter starts the run and moves focus to the court, so Space shoots at once.
  await page.keyboard.press("Enter");
  await expect(powerMeter(page)).toBeVisible();
  await expectFocusVisible(court);
  await page.keyboard.down("Space");
  await expect(powerMeter(page)).not.toHaveAttribute("aria-valuenow", "0");
  await page.keyboard.up("Space");
  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
});

test("keyboard only: Space on Reduce motion during a run toggles it and does not shoot", async ({ page }) => {
  await openStreetHoops(page);
  await startButton(page).focus();
  await page.keyboard.press("Enter");
  await expect(powerMeter(page)).toBeVisible();

  const reduceMotion = page.getByRole("checkbox", { name: "Reduce motion" });
  const initiallyReduced = await reduceMotion.isChecked();
  await page.keyboard.press("Tab");
  await expectFocusVisible(reduceMotion);
  await page.keyboard.press("Space");

  await expect(reduceMotion).toBeChecked({ checked: !initiallyReduced });
  await expect(page.getByText(SHOT_RESULT)).toHaveCount(0);
});
