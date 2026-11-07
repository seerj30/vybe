import { AUTH_ANON_KEY, AUTH_URL } from "../fake-auth/config.ts";
import { accessToken, confirmationUrl, createUser, updateUser } from "../fake-auth/users.ts";
import { bearer, confirmLink, fillSignIn, guestPost, json, openNav, signInOnLanding } from "./account-flows.ts";
import { ADULT_GUEST, expect, newPlayer, passAgeGateDialog, walletBalance } from "./app.ts";
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
  await grantCoins(page, 600);
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
  const account = page.getByRole("region", { name: "Account", exact: true });
  await fillSignIn(account, user);
  // Nothing is linked before the explicit confirmation.
  await expect(account.getByRole("button", { name: "Link this account" })).toBeVisible();
  expect(await json<Me>(await page.request.get("/api/v1/me"))).toMatchObject({ id: guestId, kind: "guest" });
  await confirmLink(account, user.email);
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
  const created = await json<{ playerId: string }>(await guestPost(first.request, "/guests", ADULT_GUEST));
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
  await confirmLink(page, user.email);
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
  await expect(region.getByText("a new VYBE player")).toBeVisible();
  await region.getByRole("button", { name: "Link this account" }).click();
  await passAgeGateDialog(page); // a new player passes the age gate first (spec §17 decision 1)
  await expect(page.getByRole("heading", { name: "Create your avatar" })).toBeVisible();

  // The session is in memory only: a reload signs out, and the app asks to sign in (not "lost guest").
  await page.goto("/hub");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Start as guest" })).toBeVisible();
});

/** Sign-up with "Confirm email" on: the page asks to check the email. */
async function signUpUnconfirmed(page: import("@playwright/test").Page, email: string): Promise<void> {
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  const region = page.getByRole("region", { name: "Sign in to VYBE" });
  await region.getByRole("button", { name: "Create an account instead" }).click();
  await region.getByLabel("Email").fill(email);
  await region.getByLabel("Password").fill("a-long-password");
  await region.getByRole("button", { name: "Create account" }).click();
  await expect(region.getByRole("status")).toContainText("Check your email");
}

test("email confirmation (PKCE): the link signs in only the tab that signed up; the link needs a confirmation", async ({ page }) => {
  await newPlayer(page);
  const guestId = await playerId(page);
  const email = `e2e-${crypto.randomUUID()}+unconfirmed@vybe.test`;
  await signUpUnconfirmed(page, email);

  // The email link: Auth confirms the address and redirects to the app with a one-time code.
  await page.goto(await confirmationUrl(email));
  await confirmLink(page, email);
  await expect(page.getByRole("heading", { name: "Hub" })).toBeVisible();
  const me = await json<Me>(await page.request.get("/api/v1/me", { headers: bearer(await accessToken({ id: "", email, password: "a-long-password" })) }));
  expect(me).toMatchObject({ id: guestId, kind: "registered" });
});

test("a sign-in link with someone else's session does not sign in and links nothing (login CSRF)", async ({ page, browser }) => {
  // The attacker starts a sign-up in their own browser and keeps the code of the confirmation link.
  const attacker = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  const email = `e2e-${crypto.randomUUID()}+unconfirmed@vybe.test`;
  await signUpUnconfirmed(attacker, email);
  const redirect = await fetch(await confirmationUrl(email), { redirect: "manual" });
  const codeLink = redirect.headers.get("Location") ?? "";
  expect(codeLink).toMatch(/[?&]code=/);
  const session = await json<Record<string, string | number>>(
    await attacker.request.post(`${AUTH_URL}/auth/v1/token?grant_type=password`, {
      headers: { apikey: AUTH_ANON_KEY },
      data: { email, password: "a-long-password" },
    }),
  );
  await attacker.close();

  // The victim, a guest, opens the attacker's links: a PKCE code without this tab's verifier, and an implicit-flow fragment.
  await newPlayer(page);
  const guest = await json<Me>(await page.request.get("/api/v1/me"));
  const fragment = new URLSearchParams({
    access_token: String(session["access_token"]),
    refresh_token: String(session["refresh_token"]),
    expires_in: "3600",
    token_type: "bearer",
    type: "signup",
  });
  // A foreign code: this tab has no verifier, so the page asks for a password sign-in instead.
  await page.goto(new URL(codeLink).pathname + new URL(codeLink).search);
  await expect(page.getByRole("alert")).toContainText("works only in the browser tab where you created the account");
  await expect(page).not.toHaveURL(/code=/);
  await expect(page.getByRole("link", { name: `Continue as ${guest.handle}` })).toBeVisible();
  await expect(page.getByRole("button", { name: "Link this account" })).toHaveCount(0);
  // An implicit-flow fragment: ignored.
  await page.goto(`/#${fragment.toString()}`);
  await expect(page.getByRole("link", { name: `Continue as ${guest.handle}` })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Link this account" })).toHaveCount(0);

  expect(await json<Me>(await page.request.get("/api/v1/me"))).toMatchObject({ id: guest.id, kind: "guest" });
  const attackerMe = await page.request.get("/api/v1/me", { headers: bearer(String(session["access_token"])) });
  expect(attackerMe.status()).toBe(404); // the attacker's identity got no player
});

test("an email link opened in another tab explains the password sign-in, which then links", async ({ page, context }) => {
  await newPlayer(page);
  const guestId = await playerId(page);
  const email = `e2e-${crypto.randomUUID()}+unconfirmed@vybe.test`;
  await signUpUnconfirmed(page, email);

  const other = await context.newPage(); // the email client opens a new tab: no PKCE verifier there
  await other.goto(await confirmationUrl(email));
  await expect(other.getByRole("alert")).toContainText("works only in the browser tab where you created the account");
  await fillSignIn(other.getByRole("region", { name: "Sign in to VYBE" }), { id: "", email, password: "a-long-password" });
  await confirmLink(other, email);
  await expect(other.getByRole("heading", { name: "Hub" })).toBeVisible();
  const me = await json<Me>(await other.request.get("/api/v1/me", { headers: bearer(await accessToken({ id: "", email, password: "a-long-password" })) }));
  expect(me).toMatchObject({ id: guestId, kind: "registered" });
});

test("sign-out forgets the player: the landing page shows no old handle", async ({ page }) => {
  await newPlayer(page);
  const guest = await json<Me>(await page.request.get("/api/v1/me"));
  const user = await createUser();
  await openNav(page, "Settings");
  const account = page.getByRole("region", { name: "Account", exact: true });
  await fillSignIn(account, user);
  await confirmLink(account, user.email);
  await expect(page.getByTestId("account-kind")).toHaveText("Linked account");

  await account.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("link", { name: "Start as guest" })).toBeVisible();
  await expect(page.getByText(guest.handle)).toHaveCount(0);
  // A local sign-out: the account's other sessions keep working.
  expect((await page.request.get("/api/v1/me", { headers: bearer(await accessToken(user)) })).status()).toBe(200);
});

test("the app runs under a Content Security Policy that allows only the app and Supabase", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Start as guest" })).toBeVisible();
  const policy = await page.locator('meta[http-equiv="Content-Security-Policy"]').getAttribute("content");
  expect(policy).toContain(`connect-src 'self' ${AUTH_URL}`);
  expect(policy).toContain("object-src 'none'");
  const blocked = await page.evaluate(async () => {
    const violation = new Promise<string>((resolve) => document.addEventListener("securitypolicyviolation", (e) => resolve(e.violatedDirective), { once: true }));
    await fetch("http://127.0.0.1:9/").catch(() => undefined);
    return violation;
  });
  expect(blocked).toBe("connect-src");
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
  const target = await json<{ handle: string }>(await guestPost(other.request, "/guests", ADULT_GUEST));
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
