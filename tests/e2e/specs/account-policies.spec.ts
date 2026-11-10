import pg from "pg";
import { accessToken, createUser } from "../fake-auth/users.ts";
import { fillSignIn, openNav } from "./account-flows.ts";
import { expect, fillAgeGate, newPlayer } from "./app.ts";
import { DATABASE_URL, playerId } from "./db.ts";
import { me, otherPlayer } from "./social.ts";
import { test } from "./street-hoops.ts";

// Spec §17 decisions 1, 2 and 6 (spec v1.4) in a browser: age gate, guest expiry, profile visibility.

async function sql(text: string, values: unknown[]): Promise<void> {
  const client = new pg.Client({ connectionString: DATABASE_URL });
  await client.connect();
  try {
    await client.query(text, values);
  } finally {
    await client.end();
  }
}

test("age gate: the form gives no hint, and an under-age answer creates no player and no cookie", async ({ page }) => {
  await page.goto("/play");
  await expect(page.getByRole("group", { name: "Date of birth" })).toBeVisible();
  await expect(page.getByLabel("Month")).toHaveValue("");
  await expect(page.getByLabel("Year")).toHaveValue("");
  await page.getByLabel("Year").fill("1990");
  await page.getByRole("button", { name: "Start as guest" }).click();
  await expect(page.getByText("Choose the month and enter the year with 4 digits.")).toBeVisible();
  await expect(page.getByLabel("Month")).toBeFocused();

  await fillAgeGate(page, { month: "January", year: new Date().getUTCFullYear() - 10 });
  await page.getByRole("button", { name: "Start as guest" }).click();
  await expect(page.getByTestId("age-not-eligible")).toContainText("VYBE is only for adults.");
  await expect(page.getByLabel("Year")).toHaveCount(0); // no second try on this page

  expect((await page.context().cookies()).map((c) => c.name)).not.toContain("__Host-vybe_guest");
  expect((await page.request.get("/api/v1/me")).status()).toBe(401);
  await page.goto("/hub");
  await expect(page).toHaveURL(/\/play$/);
});

test("guest expiry: the hub shows the end of the guest session, and a link prompt in the last 14 days", async ({ page }) => {
  await newPlayer(page);
  await expect(page.getByTestId("guest-expiry")).toContainText("Guest progress is kept until");

  // An active guest created 80 days ago: the 90-day cap leaves 10 days, so a renewal cannot help.
  const id = await playerId(page);
  await sql("UPDATE vybe.players SET created_at = now() - interval '80 days' WHERE id = $1", [id]);
  await sql("UPDATE vybe.guest_sessions SET expires_at = now() + interval '10 days' WHERE player_id = $1", [id]);
  await page.reload();
  const prompt = page.getByTestId("guest-expiry");
  await expect(prompt).toContainText(/Your guest progress ends in (9|10) days/);
  await prompt.getByRole("link", { name: "Link an account" }).click();
  await expect(page.getByRole("heading", { name: "Link an account" })).toBeVisible();
});

test("profiles: signed-in players only; a private profile is visible to its owner only", async ({ page, browser }) => {
  await newPlayer(page);
  const owner = await me(page.request);
  const viewer = await otherPlayer(browser);

  // Anonymous callers get the 404 of an unknown handle; search engines never index profiles.
  const anonymous = await browser.newContext();
  const response = await anonymous.request.get(`/api/v1/profiles/${owner.handle}`);
  expect(response.status()).toBe(404);
  expect(response.headers()["x-robots-tag"]).toBe("noindex");
  expect(response.headers()["cache-control"]).toBe("private, no-store");
  await anonymous.close();

  await viewer.goto(`/profile/${owner.handle}`);
  await expect(viewer.getByText(`${owner.handle} · Level`)).toBeVisible();

  await openNav(page, "Settings");
  const privacy = page.getByRole("region", { name: "Privacy" });
  await privacy.getByRole("radio", { name: /Only me \(private\)/ }).check();
  await privacy.getByRole("button", { name: "Save" }).click();
  await expect(privacy.getByText("Your profile is now private.")).toBeVisible();

  await viewer.reload();
  await expect(viewer.getByText("This profile is not available.")).toBeVisible();
  await page.goto(`/profile/${owner.handle}`);
  await expect(page.getByTestId("own-profile-private")).toBeVisible();

  await openNav(page, "Settings");
  await expect(privacy.getByRole("radio", { name: /Only me \(private\)/ })).toBeChecked();
  await privacy.getByRole("radio", { name: /Signed-in players/ }).check();
  await privacy.getByRole("button", { name: "Save" }).click();
  await expect(privacy.getByText("Signed-in players can now see your profile.")).toBeVisible();
  await viewer.reload();
  await expect(viewer.getByText(`${owner.handle} · Level`)).toBeVisible();
  await viewer.context().close();
});

const UNDER_AGE = { month: "January", year: new Date().getUTCFullYear() - 12 };
/** The privacy worker runs every second in the tests (Privacy__PollSeconds). */
const WORKER = { timeout: 20_000 };

test("sign-up without a guest: the age gate comes first, and an under-age answer creates no Auth user", async ({ page }) => {
  const email = `e2e-${crypto.randomUUID()}@vybe.test`;
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  const region = page.getByRole("region", { name: "Sign in to VYBE" });
  await region.getByRole("button", { name: "Create an account instead" }).click();
  await region.getByLabel("Email").fill(email);
  await region.getByLabel("Password").fill("a-long-password");
  await region.getByRole("button", { name: "Create account" }).click();

  const dialog = page.getByRole("dialog", { name: "Before you play" });
  await fillAgeGate(dialog, UNDER_AGE);
  await dialog.getByRole("button", { name: "Continue" }).click();
  await expect(region.getByRole("alert")).toContainText("VYBE is only for adults");

  await expect(accessToken({ id: "", email, password: "a-long-password" })).rejects.toThrow(); // no Auth user
  expect((await page.context().cookies()).map((c) => c.name)).not.toContain("__Host-vybe_guest");
});

test("link without a guest: an under-age answer signs out and deletes the Auth user", async ({ page }) => {
  const user = await createUser();
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  const region = page.getByRole("region", { name: "Sign in to VYBE" });
  await fillSignIn(region, user);
  await expect(region.getByText("a new VYBE player")).toBeVisible();
  await region.getByRole("button", { name: "Link this account" }).click();

  const dialog = page.getByRole("dialog", { name: "Before you play" });
  await fillAgeGate(dialog, UNDER_AGE);
  await dialog.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("alert")).toContainText("VYBE is only for adults");
  await expect(page.getByTestId("link-email")).toHaveCount(0); // nothing left to link
  expect((await page.context().cookies()).map((c) => c.name)).not.toContain("__Host-vybe_guest");

  // The API queued the Auth user (J4 queue); the privacy worker deletes it.
  await expect.poll(() => accessToken(user).then(() => "signed in", () => "no user"), WORKER).toBe("no user");
});
