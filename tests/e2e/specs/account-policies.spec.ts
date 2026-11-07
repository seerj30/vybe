import pg from "pg";
import { openNav } from "./account-flows.ts";
import { expect, newPlayer } from "./app.ts";
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
  const year = page.getByLabel("Year of birth");
  await expect(year).toHaveValue("");
  await year.fill("19");
  await page.getByRole("button", { name: "Start as guest" }).click();
  await expect(page.getByText("Enter the year with 4 digits.")).toBeVisible();
  await expect(year).toBeFocused();

  await year.fill(String(new Date().getUTCFullYear() - 10));
  await page.getByRole("button", { name: "Start as guest" }).click();
  await expect(page.getByTestId("age-not-eligible")).toContainText("VYBE is only for adults.");
  await expect(page.getByLabel("Year of birth")).toHaveCount(0); // no second try on this page

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
