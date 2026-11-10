import type { Locator, Page } from "@playwright/test";
import { expect, newPlayer, openOnlineStreetHoops } from "./app.ts";
import { powerMeter, startButton, test } from "./street-hoops.ts";

/**
 * Visual regression of the main screens (spec §14), at desktop and phone sizes (one Playwright project each,
 * playwright.config.ts). Stable baselines: every test makes a new guest with the default avatar and the seeded
 * catalog in name order; locale, time zone and reduced motion are fixed by the project; CSS animations are off;
 * the handle and dates get fixed text, the HUD clock and score are masked, and the game canvas (random basket path
 * per seed) is hidden. Baselines: tests/e2e/specs/__screenshots__/<project>/. Update: docs/runbooks/visual-baselines.md.
 */

/**
 * Waits for web fonts and for each started image to load or fail, so a screenshot never catches a half-loaded
 * page. A lazy image below the fold never starts: the wait for one image ends after 5 s.
 */
async function settled(page: Page): Promise<void> {
  await page.waitForLoadState("networkidle");
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all(
      Array.from(document.images)
        .filter((image) => !image.complete)
        .map(
          (image) =>
            new Promise((resolve) => {
              image.addEventListener("load", resolve, { once: true });
              image.addEventListener("error", resolve, { once: true });
              setTimeout(resolve, 5_000);
            }),
        ),
    );
  });
}

/**
 * Replaces per-run text with fixed text: the random handle (its length changes the layout, so a mask is not
 * enough) and dates such as the guest expiry ("Dec 10, 2026").
 */
async function fixText(page: Page): Promise<void> {
  await page.evaluate(() => {
    for (const element of Array.from(document.querySelectorAll('.handle, [data-testid="account-handle"]'))) element.textContent = "visual_guest";
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const text = node.textContent ?? "";
      const fixed = text.replace(/\b[A-Z][a-z]{2} \d{1,2}, \d{4}\b/g, "Jan 1, 2030");
      if (fixed !== text) node.textContent = fixed;
    }
  });
}

/**
 * The catalog comes in id order, and every new database gives new ids. Test data: the shop gets the same
 * items sorted by name, so the order is the same in every run.
 */
async function sortCatalog(page: Page): Promise<void> {
  await page.route("**/api/v1/catalog/items*", async (route) => {
    const response = await route.fetch();
    const body = (await response.json()) as { items: { name: string }[] };
    body.items.sort((a, b) => a.name.localeCompare(b.name, "en"));
    await route.fulfill({ response, json: body });
  });
}

async function snap(page: Page, name: string, mask: Locator[] = []): Promise<void> {
  await settled(page);
  await fixText(page);
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
  await snap(page, "hub");
});

test("shop", async ({ page }) => {
  await newPlayer(page);
  await sortCatalog(page);
  await page.goto("/shop");
  await expect(page.getByRole("heading", { name: "Shop" })).toBeVisible();
  await expect(page.getByRole("listitem").first()).toBeVisible();
  await snap(page, "shop");
});

test("wardrobe", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/wardrobe");
  await expect(page.getByRole("heading", { name: "Wardrobe" })).toBeVisible();
  await snap(page, "wardrobe");
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
  await snap(page, "settings");
});
