import type { Locator, Page } from "@playwright/test";
import { expect, newPlayer } from "./app.ts";
import { grantCoins } from "./db.ts";
import { test } from "./street-hoops.ts";

/**
 * The 3D avatar (spec §9, §10): equipping shows the item, a failed asset falls back to the starter item or the
 * 2D portrait without changing what the server says the player wears (E10), and the avatar stays inside the
 * budgets on a mid-range phone profile. The viewport reports its state and render numbers as data attributes.
 */

const ITEMS_FAILED = "Some items could not load, so starter items show instead. What you wear did not change.";
const BASE_FAILED = "The 3D avatar could not load, so a 2D picture shows instead.";

function viewport(page: Page): Locator {
  return page.locator("vybe-avatar-viewport");
}

async function expect3d(page: Page): Promise<void> {
  await expect(viewport(page)).toHaveAttribute("data-state", "3d", { timeout: 20_000 });
}

async function buyAndWearJersey(page: Page): Promise<void> {
  await grantCoins(page, 500);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Shop" }).click();
  const jersey = page.getByRole("listitem", { name: "Court Jersey" });
  await jersey.getByRole("button", { name: "Buy" }).click();
  await jersey.getByRole("button", { name: "Confirm purchase" }).click();
  await expect(jersey.getByText("Owned")).toBeVisible();
  await page.goto("/wardrobe");
  await page.getByRole("button", { name: "Wear Court Jersey" }).click();
  await expect(page.getByText("You now wear Court Jersey.")).toBeVisible();
}

test("the hub and the avatar creator show the 3D avatar in its starter items", async ({ page }) => {
  await newPlayer(page);
  await expect3d(page);
  await expect(viewport(page)).toHaveAttribute("data-items", "top_starter_tee bottom_starter_shorts shoes_starter_kicks");
  await expect(page.getByRole("img", { name: /^Avatar: Body A, .*, wearing Starter Tee, Starter Shorts, Starter Kicks$/ })).toBeVisible();

  await page.getByRole("link", { name: "Edit avatar" }).click();
  await expect3d(page);
  await page.getByText("Body B", { exact: true }).click();
  await expect(page.getByRole("img", { name: /^Avatar: Body B/ })).toBeVisible();
  // Keyboard: the turn buttons are reachable and named.
  await page.getByRole("button", { name: "Turn the avatar left" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("button", { name: "Turn the avatar right" })).toBeEnabled();
});

test("equipping an item shows it on the 3D avatar in the wardrobe and on the hub", async ({ page }) => {
  await newPlayer(page);
  await buyAndWearJersey(page);
  await expect(viewport(page)).toHaveAttribute("data-items", /\btop_court_jersey\b/);
  await expect(viewport(page)).not.toHaveAttribute("data-items", /\btop_starter_tee\b/);

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Hub" }).click();
  await expect3d(page);
  await expect(viewport(page)).toHaveAttribute("data-items", /\btop_court_jersey\b/);
});

test("the shop preview tries an item on without buying it", async ({ page }) => {
  await newPlayer(page);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Shop" }).click();
  await page.getByRole("listitem", { name: "Varsity Jacket" }).getByRole("button", { name: "Preview Varsity Jacket" }).click();
  await expect(page.getByRole("heading", { name: "Preview: Varsity Jacket" })).toBeFocused();
  await expect3d(page);
  await expect(viewport(page)).toHaveAttribute("data-items", /\btop_varsity_jacket\b/);
  await page.getByRole("button", { name: "Close preview" }).click();
  await expect(viewport(page)).toHaveCount(0);
  const inventory = (await (await page.request.get("/api/v1/me/inventory")).json()) as { items: { sku: string }[] };
  expect(inventory.items.map((i) => i.sku)).not.toContain("top_varsity_jacket");
});

test("E10: an item file fails to load → the starter item shows with a notice; equipment stays", async ({ page }) => {
  await newPlayer(page);
  await buyAndWearJersey(page);
  await page.route("**/assets/3d/items/court_jersey.*", (route) => route.fulfill({ status: 404, body: "" }));
  await page.reload();

  await expect3d(page);
  await expect(viewport(page)).toHaveAttribute("data-items", /\btop_starter_tee\b/);
  await expect(viewport(page)).not.toHaveAttribute("data-items", /\btop_court_jersey\b/);
  await expect(page.getByText(ITEMS_FAILED)).toBeVisible();
  // The server state did not change: the jersey is still worn.
  await expect(page.getByRole("button", { name: "Take off Court Jersey" })).toBeVisible();
  const avatar = (await (await page.request.get("/api/v1/me/avatar")).json()) as { equipped: { top: string | null } };
  const inventory = (await (await page.request.get("/api/v1/me/inventory")).json()) as { items: { itemId: string; sku: string }[] };
  expect(inventory.items.find((i) => i.itemId === avatar.equipped.top)?.sku).toBe("top_court_jersey");
});

test("E10: the base avatar file fails to load → the 2D portrait with a notice", async ({ page }) => {
  await page.route("**/assets/3d/avatar/**", (route) => route.fulfill({ status: 404, body: "" }));
  await newPlayer(page);
  await expect(viewport(page)).toHaveAttribute("data-state", "fallback");
  await expect(page.getByRole("img", { name: /^Avatar: Body A/ })).toBeVisible();
  await expect(page.getByText(BASE_FAILED)).toBeVisible();
  await expect(page.getByRole("button", { name: "Turn the avatar left" })).toHaveCount(0);
});

test.describe("mid-range phone profile", () => {
  test.use({ viewport: { width: 375, height: 812 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });

  test("the avatar loads within the budgets (spec §9) on a 4x slower CPU", async ({ page, browserName }) => {
    test.skip(browserName !== "chromium", "CPU throttling uses the Chromium DevTools protocol");
    await newPlayer(page);
    let assetBytes = 0;
    page.on("response", async (response) => {
      if (!response.url().includes("/assets/3d/") || response.url().includes("/thumbs/")) return;
      assetBytes += (await response.body().catch(() => Buffer.alloc(0))).length;
    });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send("Emulation.setCPUThrottlingRate", { rate: 4 });

    const started = Date.now();
    await page.reload();
    await expect3d(page);
    const loadMs = Date.now() - started;

    const triangles = Number(await viewport(page).getAttribute("data-triangles"));
    const drawCalls = Number(await viewport(page).getAttribute("data-draw-calls"));
    expect(triangles).toBeGreaterThan(0);
    expect(triangles).toBeLessThanOrEqual(25_000);
    expect(drawCalls).toBeLessThanOrEqual(60);
    expect(assetBytes).toBeGreaterThan(0);
    expect(assetBytes).toBeLessThanOrEqual(2_000_000);
    expect(loadMs).toBeLessThan(15_000);
    test.info().annotations.push({ type: "avatar-budget", description: `${loadMs} ms, ${triangles} triangles, ${drawCalls} draw calls, ${assetBytes} bytes` });
  });
});
