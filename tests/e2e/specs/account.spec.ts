import { accessToken, createUser, updateUser } from "../fake-auth/users.ts";
import { bearer, fillSignIn, guestPost, json, openNav, signInOnLanding } from "./account-flows.ts";
import { expect, newPlayer, walletBalance } from "./app.ts";
import { grantCoins, playerId } from "./db.ts";
import { test } from "./street-hoops.ts";

interface Me {
  id: string;
  handle: string;
  kind: "guest" | "registered";
  walletBalance: number;
}

interface Inventory {
  items: { name: string }[];
}

test("E03: guest → sign in → link → same player id and inventory", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 400);
  await openNav(page, "Shop");
  const jersey = page.getByRole("listitem", { name: "Court Jersey" });
  await jersey.getByRole("button", { name: "Buy" }).click();
  await jersey.getByRole("button", { name: "Confirm purchase" }).click();
  await expect(jersey.getByText("Owned")).toBeVisible();
  const guestId = await playerId(page);
  const guest = await json<Me>(await page.request.get("/api/v1/me"));

  const user = await createUser();
  await openNav(page, "Settings");
  await expect(page.getByTestId("account-kind")).toHaveText("Guest");
  await fillSignIn(page.getByRole("region", { name: "Account", exact: true }), user);
  await expect(page.getByText(`You are signed in as ${guest.handle}.`)).toBeVisible();
  await expect(page.getByTestId("account-kind")).toHaveText("Linked account");
  await expect(page.getByTestId("account-handle")).toHaveText(guest.handle);

  // The API sees the same player under the Supabase identity; the guest cookie no longer works.
  const token = await accessToken(user);
  const me = await json<Me>(await page.request.get("/api/v1/me", { headers: bearer(token) }));
  expect(me).toMatchObject({ id: guestId, handle: guest.handle, kind: "registered", walletBalance: 100 });
  const inventory = await json<Inventory>(await page.request.get("/api/v1/me/inventory", { headers: bearer(token) }));
  expect(inventory.items.map((item) => item.name)).toContain("Court Jersey");
  expect((await page.request.get("/api/v1/me")).status()).toBe(401);

  // The signed-in app keeps working on the bearer token.
  await openNav(page, "Wallet");
  await expect(walletBalance(page)).toHaveText("100");
});

test("an identity with a player and another guest: the user chooses, nothing merges", async ({ page, browser }) => {
  // The account's player: a guest that linked earlier in another browser.
  const user = await createUser();
  const first = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const created = await json<{ playerId: string }>(await guestPost(first.request, "/guests", undefined));
  await json(await guestPost(first.request, "/accounts/link", { linkProof: await accessToken(user) }));
  await first.close();

  await newPlayer(page);
  const guest = await json<Me>(await page.request.get("/api/v1/me"));
  await signInOnLanding(page, user);
  await expect(page.getByText("This account already has the VYBE player")).toBeVisible();
  await page.getByRole("button", { name: `Keep playing as ${guest.handle}` }).click();
  await expect(page.getByRole("heading", { name: "Hub" })).toBeVisible();

  // Both players stay apart; linking the same identity to this guest is a conflict (409).
  expect(await json<Me>(await page.request.get("/api/v1/me"))).toMatchObject({ id: guest.id, kind: "guest" });
  const account = await json<Me>(await page.request.get("/api/v1/me", { headers: bearer(await accessToken(user)) }));
  expect(account.id).toBe(created.playerId);
  const conflict = await guestPost(page.request, "/accounts/link", { linkProof: await accessToken(user) });
  expect(conflict.status()).toBe(409);
  expect(((await conflict.json()) as { code: string }).code).toBe("identity_already_linked");
});

test("an Auth outage during linking shows a retry; the guest stays valid", async ({ page }) => {
  await newPlayer(page);
  const guestId = await playerId(page);
  const user = await createUser();
  await updateUser(user, { userFailures: 1 });

  await signInOnLanding(page, user);
  await expect(page.getByRole("alert")).toContainText("The sign-in service does not answer right now");
  expect(await playerId(page)).toBe(guestId);

  await page.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("heading", { name: "Hub" })).toBeVisible();
  const me = await json<Me>(await page.request.get("/api/v1/me", { headers: bearer(await accessToken(user)) }));
  expect(me).toMatchObject({ id: guestId, kind: "registered" });
});

test("a new account without a guest gets a new player; a reload asks to sign in again", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  const region = page.getByRole("region", { name: "Sign in to VYBE" });
  await region.getByRole("button", { name: "Create an account instead" }).click();
  const form = region.getByRole("form", { name: "Create an account" });
  await form.getByLabel("Email").fill(`e2e-${crypto.randomUUID()}@vybe.test`);
  await form.getByLabel("Password").fill("a-long-password");
  await form.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByRole("heading", { name: "Create your avatar" })).toBeVisible();

  // The session is in memory only: a reload signs out, and the app asks to sign in (not "lost guest").
  await page.goto("/hub");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Start as guest" })).toBeVisible();
});

test("an account with an unconfirmed email waits for the confirmation link", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  const region = page.getByRole("region", { name: "Sign in to VYBE" });
  await region.getByRole("button", { name: "Create an account instead" }).click();
  await region.getByLabel("Email").fill(`e2e-${crypto.randomUUID()}+unconfirmed@vybe.test`);
  await region.getByLabel("Password").fill("a-long-password");
  await region.getByRole("button", { name: "Create account" }).click();
  await expect(region.getByRole("status")).toContainText("Check your email");
});

test("a wrong password is explained and nothing changes", async ({ page }) => {
  await newPlayer(page);
  const guestId = await playerId(page);
  const user = await createUser();
  await signInOnLanding(page, { ...user, password: "not-the-password" });
  await expect(page.getByRole("alert")).toHaveText("The email or the password is not correct.");
  expect(await playerId(page)).toBe(guestId);
});

test("settings: blocked players are listed and can be unblocked", async ({ page, browser }) => {
  const other = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const target = await json<{ handle: string }>(await guestPost(other.request, "/guests", undefined));
  await other.close();

  await newPlayer(page);
  expect((await guestPost(page.request, "/me/blocks", { handle: target.handle })).status()).toBe(204);
  await openNav(page, "Settings");
  const blocks = page.getByRole("list", { name: "Blocked players" });
  await expect(blocks.getByRole("listitem", { name: target.handle })).toBeVisible();
  await blocks.getByRole("button", { name: `Unblock ${target.handle}` }).click();
  await expect(page.getByText(`You unblocked ${target.handle}.`)).toBeVisible();
  await expect(page.getByText("You have not blocked anyone.")).toBeVisible();
});
