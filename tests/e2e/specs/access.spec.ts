import { accessToken, createUser } from "../fake-auth/users.ts";
import { bearer, guestPost, json, signInOnAdmin } from "./account-flows.ts";
import { expect, newPlayer } from "./app.ts";
import { grantCoins, playerId } from "./db.ts";
import { test } from "./street-hoops.ts";

interface Me {
  id: string;
}

const ADMIN_WRITES = [
  { path: "/admin/wallet-adjustments", data: (id: string) => ({ playerId: id, delta: 100, reason: "free coins" }) },
  { path: "/admin/players/{id}/suspension", data: () => ({ reason: "no reason" }) },
];

test("E08: a player reaches only the own wallet and inventory", async ({ page, browser }) => {
  await newPlayer(page);
  await grantCoins(page, 123);
  const other = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const victim = await json<{ playerId: string }>(await guestPost(other.request, "/guests", undefined));

  // The API takes the player from the session, never from the request: there is no path with a player id.
  for (const path of [`/players/${victim.playerId}/wallet`, `/players/${victim.playerId}/inventory`, `/me/wallet/transactions?playerId=${victim.playerId}`]) {
    const response = await page.request.get(`/api/v1${path}`);
    if (response.ok()) {
      expect(JSON.stringify(await response.json())).not.toContain(victim.playerId);
    } else {
      expect(response.status()).toBe(404);
    }
  }
  const mine = await json<Me>(await page.request.get(`/api/v1/me?playerId=${victim.playerId}`));
  expect(mine.id).toBe(await playerId(page));
  const theirs = await json<{ walletBalance: number }>(await other.request.get("/api/v1/me"));
  expect(theirs.walletBalance).toBe(0);

  // Admin endpoints: a guest cookie is not enough (bearer only) and changes nothing.
  for (const write of ADMIN_WRITES) {
    const response = await page.request.post(`/api/v1${write.path.replace("{id}", victim.playerId)}`, {
      headers: { "X-Requested-With": "vybe-web", "Idempotency-Key": crypto.randomUUID() },
      data: write.data(victim.playerId),
    });
    expect(response.status()).toBe(401);
  }
  expect((await json<{ walletBalance: number }>(await other.request.get("/api/v1/me"))).walletBalance).toBe(0);
  await other.close();
});

test("E08: a normal player sees 'Unauthorized' at /admin, and the admin API refuses the token (403)", async ({ page }) => {
  const player = await createUser();
  await signInOnAdmin(page, player);
  await expect(page.getByRole("heading", { name: "Unauthorized" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Reports" })).toHaveCount(0);

  const token = await accessToken(player);
  for (const path of ["/admin/reports", "/admin/audit-logs"]) {
    expect((await page.request.get(`/api/v1${path}`, { headers: bearer(token) })).status()).toBe(403);
  }
  const adjust = await page.request.post("/api/v1/admin/wallet-adjustments", {
    headers: { ...bearer(token), "Idempotency-Key": crypto.randomUUID() },
    data: { playerId: crypto.randomUUID(), delta: 100, reason: "free coins" },
  });
  expect(adjust.status()).toBe(403);
});

test("E08: a signed-out visitor at /admin gets the staff sign-in only", async ({ page }) => {
  await page.goto("/admin");
  await expect(page.getByText("Staff accounts only.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Create an account instead" })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Reports" })).toHaveCount(0);
});

test("a moderator token cannot adjust wallets, edit the catalog or read the audit log (403)", async ({ page }) => {
  const token = await accessToken(await createUser({ role: "moderator" }));
  expect((await page.request.get("/api/v1/admin/reports", { headers: bearer(token) })).status()).toBe(200);
  expect((await page.request.get("/api/v1/admin/audit-logs", { headers: bearer(token) })).status()).toBe(403);
  const adjust = await page.request.post("/api/v1/admin/wallet-adjustments", {
    headers: { ...bearer(token), "Idempotency-Key": crypto.randomUUID() },
    data: { playerId: crypto.randomUUID(), delta: 100, reason: "free coins" },
  });
  expect(adjust.status()).toBe(403);
  const catalog = await page.request.patch(`/api/v1/admin/catalog/items/${crypto.randomUUID()}`, {
    headers: bearer(token),
    data: { isActive: false, reason: "not allowed" },
  });
  expect(catalog.status()).toBe(403);
});
