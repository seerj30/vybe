import { accessToken, createUser, updateUser } from "../fake-auth/users.ts";
import { bearer, confirmLink, fillSignIn, guestPost, json, openNav } from "./account-flows.ts";
import { ADULT_GUEST, expect, newPlayer } from "./app.ts";
import { playerId } from "./db.ts";
import { test } from "./street-hoops.ts";

/** The privacy worker runs every second in the tests (Privacy__PollSeconds), the status page polls every 3 s. */
const WORKER = { timeout: 20_000 };

test("E12: data export → ready → short-lived link → a new link; another player cannot read it", async ({ page, browser }) => {
  await newPlayer(page);
  await openNav(page, "Settings");
  const section = page.getByRole("region", { name: "Your data" });
  await section.getByRole("button", { name: "Request my data" }).click();
  await expect(section.getByText("Your export is ready.")).toBeVisible(WORKER);
  const link = section.getByRole("link", { name: "Download your data" });
  await expect(link).toHaveAttribute("href", /.+/);
  await expect(section.getByText(/This link works until/)).toBeVisible();

  await section.getByRole("button", { name: "Get a new download link" }).click();
  await expect(link).toBeVisible();
  // One ready export per 24 hours.
  await section.getByRole("button", { name: "Request my data" }).click();
  await expect(section.getByRole("alert")).toContainText("one data export every 24 hours");

  const exportId = await page.evaluate(async (id) => localStorage.getItem(`vybe.export.${id}`), await playerId(page));
  expect(exportId).not.toBeNull();
  const other = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  await guestPost(other.request, "/guests", ADULT_GUEST);
  expect((await other.request.get(`/api/v1/me/data-export/${exportId}`)).status()).toBe(404);
  await other.close();
});

test("E12: a guest deletes the account; the status page needs no session", async ({ page, browser }) => {
  await newPlayer(page);
  await openNav(page, "Settings");
  const section = page.getByRole("region", { name: "Delete account" });
  await section.getByRole("button", { name: "Delete my account…" }).click();
  const confirm = section.getByRole("button", { name: "Delete my account", exact: true });
  await expect(confirm).toBeDisabled();
  await section.getByLabel("I understand").check();
  await confirm.click();

  await expect(page.getByRole("heading", { name: "Account deletion" })).toBeVisible();
  // The address holds the request id (a capability): it must not leak through the Referer header.
  await expect(page.locator('meta[name="referrer"]')).toHaveAttribute("content", "no-referrer");
  await expect(page.getByTestId("deletion-status")).toContainText("Your account is deleted", WORKER);
  expect(await page.evaluate(() => localStorage.getItem("vybe.deletionRequest"))).toBeNull();
  const requestId = new URL(page.url()).pathname.split("/").pop() ?? "";

  // The guest session is revoked; the status needs only the request id.
  expect((await page.request.get("/api/v1/me")).status()).toBe(401);
  const stranger = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const status = await json<{ status: string }>(await stranger.request.get(`/api/v1/deletion-requests/${requestId}`));
  expect(status.status).toBe("completed");
  const strangerPage = await stranger.newPage();
  await strangerPage.goto(`/deletion/${crypto.randomUUID()}`);
  await expect(strangerPage.getByRole("alert")).toHaveText("VYBE has no deletion request with this id.");
  await stranger.close();
});

test("E12: a registered player confirms the deletion with a new sign-in (verification required)", async ({ page }) => {
  await newPlayer(page);
  // The sign-in is 10 minutes old: older than the 5 minutes the API accepts for a deletion.
  const user = await createUser({ amrAgeSeconds: 600 });
  await openNav(page, "Settings");
  const account = page.getByRole("region", { name: "Account", exact: true });
  await fillSignIn(account, user);
  await confirmLink(account, user.email);
  await expect(page.getByTestId("account-kind")).toHaveText("Linked account");

  const section = page.getByRole("region", { name: "Delete account" });
  await section.getByRole("button", { name: "Delete my account…" }).click();
  await section.getByLabel("I understand").check();
  await section.getByRole("button", { name: "Delete my account", exact: true }).click();
  await expect(section.getByRole("heading", { name: "Verification required" })).toBeVisible();

  await updateUser(user, { amrAgeSeconds: 0 });
  const token = await accessToken(user);
  await section.getByLabel("Password").fill(user.password);
  await section.getByRole("button", { name: "Confirm and delete" }).click();
  await expect(page.getByTestId("deletion-status")).toContainText("Your account is deleted", WORKER);

  // After the anonymization the identity has no player any more.
  const after = await page.request.get("/api/v1/me", { headers: bearer(token) });
  expect(after.status()).toBe(404);
  expect(((await after.json()) as { code: string }).code).toBe("player_not_found");

  // J4 also deletes the Supabase Auth user (spec §17 decision 8): the email and password sign in no more.
  await expect.poll(() => accessToken(user).then(() => "signed in", () => "no user"), WORKER).toBe("no user");
});

test("E12: deletion without a recent sign-in is refused by the API (403 reauthentication_required)", async ({ page }) => {
  await newPlayer(page);
  const user = await createUser({ amrAgeSeconds: 600 });
  await json(await guestPost(page.request, "/accounts/link", { linkProof: await accessToken(user) }));
  const response = await page.request.post("/api/v1/me/deletion-requests", {
    headers: { ...bearer(await accessToken(user)), "Idempotency-Key": crypto.randomUUID(), "X-Requested-With": "vybe-web" },
  });
  expect(response.status()).toBe(403);
  expect(((await response.json()) as { code: string }).code).toBe("reauthentication_required");
});
