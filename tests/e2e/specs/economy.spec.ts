import type { APIRequestContext, Locator, Page } from "@playwright/test";
import { expect, newPlayer } from "./app.ts";
import { grantCoins } from "./db.ts";
import { test } from "./street-hoops.ts";

interface Me {
  walletBalance: number;
}

function shopItem(page: Page, name: string): Locator {
  return page.getByRole("listitem", { name });
}

async function openShop(page: Page): Promise<void> {
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Shop" }).click();
  await expect(shopItem(page, "Court Jersey")).toBeVisible();
}

async function confirmPurchase(page: Page, name: string): Promise<void> {
  await shopItem(page, name).getByRole("button", { name: "Buy" }).click();
  await shopItem(page, name).getByRole("button", { name: "Confirm purchase" }).click();
}

async function me(request: APIRequestContext): Promise<Me> {
  return (await (await request.get("/api/v1/me")).json()) as Me;
}

async function ledger(request: APIRequestContext): Promise<{ reason: string; delta: number }[]> {
  return ((await (await request.get("/api/v1/me/wallet/transactions")).json()) as { items: { reason: string; delta: number }[] }).items;
}

test("E02: wallet → purchase → equip → reload persists", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 400);
  await openShop(page);
  await expect(page.getByText("Balance: 400 V-Coins")).toBeVisible();

  await confirmPurchase(page, "Court Jersey");
  await expect(shopItem(page, "Court Jersey").getByText("Owned")).toBeVisible();
  await expect(page.getByText("Balance: 100 V-Coins")).toBeVisible();

  await shopItem(page, "Court Jersey").getByRole("link", { name: "Wear it" }).click();
  await page.getByRole("button", { name: "Wear Court Jersey" }).click();
  await expect(page.getByText("You now wear Court Jersey.")).toBeVisible();

  await page.reload();
  await expect(page.getByRole("button", { name: "Take off Court Jersey" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Wear Starter Tee" })).toBeVisible();

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Wallet" }).click();
  await expect(page.getByTestId("wallet-balance")).toHaveText("100");
  const rows = page.getByRole("row");
  await expect(rows.nth(1)).toContainText("Purchase");
  await expect(rows.nth(1)).toContainText("−300");
  await expect(rows.nth(2)).toContainText("Adjustment by VYBE");
  await expect(rows.nth(2)).toContainText("+400");
});

test("E06: two simultaneous purchases never overdraw the wallet", async ({ page, context }) => {
  await newPlayer(page);
  await grantCoins(page, 400);
  const other = await context.newPage();
  for (const tab of [page, other]) {
    await tab.goto("/shop");
    await expect(tab.getByText("Balance: 400 V-Coins")).toBeVisible();
  }
  await shopItem(page, "Court Jersey").getByRole("button", { name: "Buy" }).click();
  await shopItem(other, "High Top").getByRole("button", { name: "Buy" }).click();

  // 300 + 350 > 400: the server lets exactly one purchase through.
  await Promise.all([
    shopItem(page, "Court Jersey").getByRole("button", { name: "Confirm purchase" }).click(),
    shopItem(other, "High Top").getByRole("button", { name: "Confirm purchase" }).click(),
  ]);
  const jersey = shopItem(page, "Court Jersey");
  const highTop = shopItem(other, "High Top");
  await expect(jersey.getByText(/Owned|You do not have enough V-Coins/)).toBeVisible();
  await expect(highTop.getByText(/Owned|You do not have enough V-Coins/)).toBeVisible();

  const owned = [await jersey.getByText("Owned").count(), await highTop.getByText("Owned").count()];
  expect(owned.reduce((a, b) => a + b)).toBe(1);
  const balance = (await me(page.request)).walletBalance;
  expect(balance).toBe(owned[0] === 1 ? 100 : 50);
  expect((await ledger(page.request)).filter((e) => e.reason === "purchase")).toHaveLength(1);
});

test("E09: the purchase answer is lost → Check and retry finds the item, no second purchase", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 400);
  let posts = 0;
  await page.route("**/api/v1/purchases", async (route) => {
    posts++;
    if (posts > 1) return route.continue();
    // The server commits the purchase, but the answer never reaches the browser.
    const response = await route.fetch();
    expect(response.status()).toBe(200);
    await route.abort("connectionreset");
  });

  await openShop(page);
  await confirmPurchase(page, "Court Jersey");
  await expect(shopItem(page, "Court Jersey").getByRole("alert")).toHaveText(/The connection was lost/);
  await shopItem(page, "Court Jersey").getByRole("button", { name: "Check and retry" }).click();

  await expect(shopItem(page, "Court Jersey").getByText("Owned")).toBeVisible();
  expect(posts).toBe(1);
  expect((await me(page.request)).walletBalance).toBe(100);
  expect((await ledger(page.request)).filter((e) => e.reason === "purchase")).toHaveLength(1);
});

test("E09: the purchase request is lost → the retry sends the same Idempotency-Key and buys once", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 400);
  const keys: string[] = [];
  await page.route("**/api/v1/purchases", async (route) => {
    keys.push((await route.request().headerValue("idempotency-key")) ?? "");
    if (keys.length === 1) return route.abort("internetdisconnected");
    await route.continue();
  });

  await openShop(page);
  await confirmPurchase(page, "Court Jersey");
  await shopItem(page, "Court Jersey").getByRole("button", { name: "Check and retry" }).click();

  await expect(shopItem(page, "Court Jersey").getByText("Owned")).toBeVisible();
  expect(keys).toHaveLength(2);
  expect(keys[1]).toBe(keys[0]);
  expect((await me(page.request)).walletBalance).toBe(100);
});

test("E10: item pictures fail to load → placeholders and a notice; the avatar and equipment stay", async ({ page }) => {
  await page.route("**/preview.png", (route) => route.fulfill({ status: 404, body: "" }));
  await newPlayer(page);

  await openShop(page);
  await expect(page.getByRole("img", { name: "Court Jersey (picture not available)" })).toBeVisible();
  await expect(page.getByText("Some item pictures could not load. Placeholders show instead.")).toBeVisible();

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Wardrobe" }).click();
  await expect(page.getByRole("img", { name: /^Avatar: / })).toBeVisible();
  await expect(page.getByRole("img", { name: "Starter Tee (picture not available)" })).toBeVisible();
  await expect(page.getByText(/What you wear did not change/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Take off Starter Tee" })).toBeVisible();
});

test("the shop shows why an item cannot be bought; the server has the last word", async ({ page }) => {
  await newPlayer(page);
  await openShop(page);
  await expect(shopItem(page, "Court Jersey").getByRole("button", { name: "Not enough V-Coins" })).toBeDisabled();
  await expect(shopItem(page, "Gold Chain").getByRole("button", { name: "Needs level 2" })).toBeDisabled();
  // Starter items are granted, never sold.
  await expect(shopItem(page, "Starter Tee")).toHaveCount(0);
});

test("an accessory goes on one attachment point at a time", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 100);
  await openShop(page);
  await confirmPurchase(page, "Sweatband");
  await expect(shopItem(page, "Sweatband").getByText("Owned")).toBeVisible();

  await page.goto("/wardrobe");
  await page.getByRole("button", { name: "Wear on point 1 Sweatband" }).click();
  await expect(page.getByRole("button", { name: "Take off (point 1) Sweatband" })).toBeVisible();
  await page.getByRole("button", { name: "Wear on point 2 Sweatband" }).click();
  await expect(page.getByText("You already wear this item on the other attachment point.")).toBeVisible();
});
