import type { Locator, Page } from "@playwright/test";
import { expect, newPlayer, openOnlineStreetHoops } from "./app.ts";
import { powerMeter, startButton, test } from "./street-hoops.ts";

/**
 * Visual regression of the main screens (spec §14), at desktop and phone sizes (one Playwright project each,
 * playwright.config.ts). Stable baselines: every test makes a new guest with the default avatar and the seeded
 * catalog; locale, time zone and reduced motion are fixed by the project; CSS animations are off; text that
 * differs per run (handle, dates, the HUD clock and score) is masked, and the game canvas (random basket path per
 * seed) is hidden. Baselines: tests/e2e/specs/__screenshots__/<project>/. Update: docs/runbooks/visual-baselines.md.
 */

/** Waits for web fonts and images, so a screenshot never catches a half-loaded page. */
async function settled(page: Page): Promise<void> {
  await page.waitForLoadState("networkidle");
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all(
      Array.from(document.images)
        .filter((image) => !image.complete)
        .map((image) => new Promise((resolve) => image.addEventListener("load", resolve, { once: true }))),
    );
  });
}

/** Per-run text of a signed-in guest: the handle in the shell and pages, the guest expiry date. */
function playerText(page: Page): Locator[] {
  return [page.locator(".handle"), page.getByTestId("guest-expiry"), page.getByTestId("account-handle")];
}

async function snap(page: Page, name: string, mask: Locator[] = []): Promise<void> {
  await settled(page);
  await expect(page).toHaveScreenshot(`${name}.png`, { fullPage: true, mask, maskColor: "#ff00ff" });
}

test("landing", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Start as guest" })).toBeVisible();
  await snap(page, "landing");
});

test("age gate", async ({ page }) => {
  await page.goto("/play");
  await expect(page.getByRole("button", { name: "Start as guest" })).toBeVisible();
  await snap(page, "age-gate");
});

test("hub", async ({ page }) => {
  await newPlayer(page);
  await expect(page.getByTestId("wallet-balance")).toBeVisible();
  await snap(page, "hub", playerText(page));
});

test("shop", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/shop");
  await expect(page.getByRole("heading", { name: "Shop" })).toBeVisible();
  await expect(page.getByRole("listitem").first()).toBeVisible();
  await snap(page, "shop", playerText(page));
});

test("wardrobe", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/wardrobe");
  await expect(page.getByRole("heading", { name: "Wardrobe" })).toBeVisible();
  await snap(page, "wardrobe", playerText(page));
});

test("gameplay HUD", async ({ page }) => {
  await newPlayer(page);
  await openOnlineStreetHoops(page);
  await startButton(page).click();
  await expect(powerMeter(page)).toBeVisible();
  // The court fills the screen under the HUD, so it is hidden (a mask would cover the HUD too).
  await page.addStyleTag({ content: "canvas { visibility: hidden !important; }" });
  await snap(page, "gameplay-hud", [page.locator(".hud-value")]);
});

test("settings", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/settings");
  await expect(page.getByRole("heading", { name: "Settings" })).toBeVisible();
  await snap(page, "settings", playerText(page));
});
