import type { Browser } from "@playwright/test";
import { createUser } from "../fake-auth/users.ts";
import { guestPost, json, openNav, signInOnAdmin } from "./account-flows.ts";
import { ADULT_GUEST, expect, newPlayer, walletBalance } from "./app.ts";
import { playerId } from "./db.ts";
import { test } from "./street-hoops.ts";

interface Guest {
  playerId: string;
  handle: string;
}

async function newGuest(browser: Browser): Promise<Guest & { close: () => Promise<void>; report: (target: string, details: string) => Promise<void> }> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const guest = await json<Guest>(await guestPost(context.request, "/guests", ADULT_GUEST));
  return {
    ...guest,
    close: () => context.close(),
    report: async (target, details) => {
      expect((await guestPost(context.request, "/reports", { targetHandle: target, reason: "offensive_name", details })).status()).toBe(202);
    },
  };
}

test("moderator: resolves reports and suspends players; no wallet, catalog or audit functions", async ({ page, browser }) => {
  const reporter = await newGuest(browser);
  const target = await newGuest(browser);
  await reporter.report(target.handle, "Name is rude");
  const moderator = await createUser({ role: "moderator" });

  await signInOnAdmin(page, moderator);
  await expect(page.getByTestId("admin-role")).toHaveText("Moderator");
  await expect(page.getByRole("heading", { name: "Reports" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Suspensions" })).toBeVisible();
  for (const hidden of ["Wallet adjustment", "Catalog", "Audit log"]) {
    await expect(page.getByRole("heading", { name: hidden })).toHaveCount(0);
  }

  const report = page.getByRole("listitem", { name: `Report on ${target.handle}` });
  await expect(report).toContainText("Name is rude");
  await report.getByLabel("Resolution reason").fill("Name changed by support");
  await report.getByRole("button", { name: "Mark as actioned" }).click();
  await expect(page.getByText(`Report on ${target.handle} marked as actioned.`)).toBeVisible();
  await expect(report).toHaveCount(0);

  const suspensions = page.getByRole("form", { name: "Suspension" });
  await suspensions.getByLabel("Player id").fill(target.playerId);
  await suspensions.getByLabel("Reason").fill("Offensive name");
  await suspensions.getByRole("button", { name: "Suspend player" }).click();
  await expect(page.getByText(`Player ${target.playerId} is suspended with no end.`)).toBeVisible();
  await suspensions.getByLabel("Reason").fill("Appeal accepted");
  await suspensions.getByRole("button", { name: "Lift suspension" }).click();
  await expect(page.getByText(`Player ${target.playerId} is not suspended.`)).toBeVisible();

  await reporter.close();
  await target.close();
});

test("a suspended player sees the suspension in the settings", async ({ page }) => {
  await newPlayer(page);
  const id = await playerId(page);
  const moderatorPage = await page.context().browser()!.newPage({ baseURL: test.info().project.use.baseURL });
  await signInOnAdmin(moderatorPage, await createUser({ role: "moderator" }));
  const suspensions = moderatorPage.getByRole("form", { name: "Suspension" });
  await suspensions.getByLabel("Player id").fill(id);
  await suspensions.getByLabel("Reason").fill("Cheating");
  await suspensions.getByRole("button", { name: "Suspend player" }).click();
  await expect(moderatorPage.getByText(`Player ${id} is suspended with no end.`)).toBeVisible();
  await moderatorPage.close();

  await openNav(page, "Settings");
  await expect(page.getByTestId("account-status")).toHaveText("Suspended");
});

test("administrator: adjusts a wallet with a reason, edits a catalog item, reads the audit log", async ({ page }) => {
  await newPlayer(page);
  const id = await playerId(page);
  const adminPage = await page.context().browser()!.newPage({ baseURL: test.info().project.use.baseURL });
  await signInOnAdmin(adminPage, await createUser({ role: "administrator" }));
  await expect(adminPage.getByTestId("admin-role")).toHaveText("Administrator");

  const wallet = adminPage.getByRole("form", { name: "Wallet adjustment" });
  await wallet.getByLabel("Player id").fill(id);
  await wallet.getByLabel("Change in V-Coins").fill("250");
  await expect(wallet.getByRole("button", { name: "Adjust wallet" })).toBeDisabled(); // no reason yet
  await wallet.getByLabel("Reason").fill("Compensation for a lost run");
  await wallet.getByRole("button", { name: "Adjust wallet" }).click();
  await expect(adminPage.getByText(`Wallet of ${id} changed by +250. New balance: 250 V-Coins.`)).toBeVisible();

  await wallet.getByLabel("Change in V-Coins").fill("-1000");
  await wallet.getByLabel("Reason").fill("Too much");
  await wallet.getByRole("button", { name: "Adjust wallet" }).click();
  await expect(adminPage.getByRole("alert")).toHaveText("The balance cannot go below 0. Nothing changed.");

  // Catalog: Swish (an emote no other spec uses) goes inactive and back.
  const catalog = await json<{ items: { id: string; name: string }[] }>(await page.request.get("/api/v1/catalog/items?limit=100"));
  const swish = catalog.items.find((item) => item.name === "Swish");
  expect(swish).toBeDefined();
  const item = adminPage.getByRole("form", { name: "Catalog item" });
  await item.getByLabel("Item id").fill(swish!.id);
  await item.getByLabel(/Inactive/).check();
  await item.getByLabel("Reason").fill("Asset broken");
  await item.getByRole("button", { name: "Save item" }).click();
  await expect(adminPage.getByText("Swish is now inactive.")).toBeVisible();
  await item.getByLabel(/Active \(in the shop\)/).check();
  await item.getByLabel("Reason").fill("Asset fixed");
  await item.getByRole("button", { name: "Save item" }).click();
  await expect(adminPage.getByText("Swish is now active.")).toBeVisible();

  const audit = adminPage.getByRole("form", { name: "Audit log filters" });
  await audit.getByLabel("Entity id").fill(swish!.id);
  await audit.getByRole("button", { name: "Search the audit log" }).click();
  const rows = adminPage.getByRole("table", { name: "Audit log, newest first" }).getByRole("row");
  await expect(rows.nth(1)).toContainText("Asset fixed");
  await expect(rows.nth(2)).toContainText("Asset broken");
  await adminPage.close();

  // The player sees the audited adjustment in the wallet.
  await openNav(page, "Wallet");
  await expect(walletBalance(page)).toHaveText("250");
  await expect(page.getByRole("row").nth(1)).toContainText("+250");
});
