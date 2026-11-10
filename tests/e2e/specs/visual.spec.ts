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
 * Waits until the screen is complete: its ready locators (the data it loads) are visible, the web fonts are
 * loaded, and each started image has loaded or failed. A lazy image below the fold never starts: the wait for one
 * image ends after 5 s.
 */
async function settled(page: Page, ready: Locator[]): Promise<void> {
  for (const locator of ready) await expect(locator).toBeVisible();
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

async function snap(page: Page, name: string, ready: Locator[], mask: Locator[] = []): Promise<void> {
  await settled(page, ready);
  await fixText(page);
  await expect(page).toHaveScreenshot(`${name}.png`, { fullPage: true, mask, maskColor: "#ff00ff" });
}

/** Item pictures are not served in E2E (dev server), so the shop and the wardrobe always show this notice. */
const PICTURE_NOTICE = /^Some item pictures could not load/;

test("landing", async ({ page }) => {
  await page.goto("/");
  await snap(page, "landing", [page.getByRole("link", { name: "Start as guest" })]);
});

test("age gate", async ({ page }) => {
  await page.goto("/play");
  await snap(page, "age-gate", [page.getByRole("button", { name: "Start as guest" }), page.getByLabel("Year")]);
});

test("hub", async ({ page }) => {
  await newPlayer(page);
  await snap(page, "hub", [
    page.getByTestId("wallet-balance"),
    page.getByTestId("guest-expiry"),
    page.getByText("Score 1,500 points in one run"), // the daily quests loaded
  ]);
});

test("shop", async ({ page }) => {
  await newPlayer(page);
  await sortCatalog(page);
  await page.goto("/shop");
  await snap(page, "shop", [page.getByRole("heading", { name: "Swish" }), page.getByText(PICTURE_NOTICE)]);
});

test("wardrobe", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/wardrobe");
  await snap(page, "wardrobe", [page.getByRole("heading", { name: "Starter Kicks" }), page.getByText(/^No saved outfits/), page.getByText(PICTURE_NOTICE)]);
});

test("gameplay HUD", async ({ page }) => {
  await newPlayer(page);
  await openOnlineStreetHoops(page);
  await startButton(page).click();
  // The court fills the screen under the HUD, so it is hidden (a mask would cover the HUD too).
  await page.addStyleTag({ content: "canvas { visibility: hidden !important; }" });
  await snap(page, "gameplay-hud", [powerMeter(page), page.getByText("Reduce motion")], [page.locator(".hud-value")]);
});

test("settings", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/settings");
  await snap(page, "settings", [
    page.getByTestId("account-handle"),
    page.getByText("You have not blocked anyone."), // the block list loaded
    page.getByRole("button", { name: "Request my data" }),
  ]);
});
