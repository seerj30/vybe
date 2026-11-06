import { test as base, expect, type Locator, type Page } from "@playwright/test";

/** Playwright `test` that fails when the page throws an uncaught error (for example in a frame callback). */
export const test = base.extend<{ failOnPageError: void }>({
  failOnPageError: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on("pageerror", (error) => errors.push(error.message));
      await use();
      expect(errors, "uncaught page errors").toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };

/** The result label after a recorded shot, e.g. "Hit +100 · streak 1" or "Miss · streak 0". */
export const SHOT_RESULT = /\b(Perfect|Hit|Miss)( \+\d+)? · streak \d+/;
export const TOO_SOON = "Too soon: wait for the meter";

export function startButton(page: Page): Locator {
  return page.getByRole("button", { name: /^(Start 60 s run|Play again)$/ });
}

export function powerMeter(page: Page): Locator {
  return page.getByRole("meter", { name: "Shot power" });
}

/** Opens the screen and waits until the ruleset checksum is verified. */
export async function openStreetHoops(page: Page): Promise<void> {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Street Hoops" })).toBeVisible();
  await expect(startButton(page)).toBeEnabled();
}

/** Starts a run and waits for the HUD power meter. */
export async function startRun(page: Page): Promise<void> {
  await startButton(page).click();
  await expect(powerMeter(page)).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
}

/** Waits until a held shot has charged above zero power. */
export async function expectCharging(page: Page): Promise<void> {
  await expect(powerMeter(page)).not.toHaveAttribute("aria-valuenow", "0");
}
