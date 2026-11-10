import { expect, newPlayer, passAgeGate, saveAvatar, startAsGuest } from "./app.ts";
import { test } from "./street-hoops.ts";

test("E01 (part): new guest → avatar saved → persists after reload → hub", async ({ page }) => {
  await startAsGuest(page);
  await page.getByRole("radio", { name: "Body B" }).check();
  await page.getByRole("radio", { name: "Curly" }).check();
  await page.getByRole("radio", { name: "Blue" }).check();
  await expect(page.getByRole("img", { name: /^Avatar: Body B, tone 3, face 1, blue curly hair$/ })).toBeVisible();
  await saveAvatar(page);

  await page.reload();
  await expect(page.getByRole("radio", { name: "Body B" })).toBeChecked();
  await expect(page.getByRole("radio", { name: "Curly" })).toBeChecked();
  await expect(page.getByRole("radio", { name: "Blue" })).toBeChecked();

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Hub" }).click();
  await expect(page.getByRole("img", { name: /blue curly hair/ })).toBeVisible();
  await expect(page.getByText(/Guest · Level 1 · 0 XP/)).toBeVisible();
  await expect(page.getByTestId("wallet-balance")).toHaveText("0");
});

test("a returning guest continues from the landing page", async ({ page }) => {
  await newPlayer(page);
  const handle = await page.getByText(/^guest_[a-z0-9]+$/).textContent();

  await page.goto("/");
  await page.getByRole("link", { name: `Continue as ${handle}` }).click();
  await expect(page.getByRole("heading", { name: "Hub" })).toBeVisible();
});

test("a lost guest cookie explains that guest progress cannot be recovered", async ({ page, context }) => {
  await newPlayer(page);
  await context.clearCookies();

  await page.goto("/hub");
  await expect(page).toHaveURL(/\/play$/);
  await expect(page.getByText(/Guest progress cannot be recovered without a linked account/)).toBeVisible();
});

test("a failed guest creation shows a retry and creates no player", async ({ page }) => {
  let failed = false;
  await page.route("**/api/v1/guests", async (route) => {
    if (failed) return route.continue();
    failed = true;
    await route.abort("connectionreset");
  });
  await page.goto("/play");
  await passAgeGate(page);
  await expect(page.getByRole("alert")).toHaveText(/No connection to VYBE/);

  await page.goto("/hub");
  await expect(page).toHaveURL(/\/play$/);

  await passAgeGate(page);
  await expect(page.getByRole("heading", { name: "Create your avatar" })).toBeVisible();
});

test("a failed avatar save keeps the choices and never shows success", async ({ page }) => {
  await startAsGuest(page);
  let failed = false;
  await page.route("**/api/v1/me/avatar", async (route) => {
    if (route.request().method() !== "PUT" || failed) return route.continue();
    failed = true;
    await route.fulfill({ status: 503, body: "Service Unavailable" });
  });

  await page.getByRole("radio", { name: "Red" }).check();
  await page.getByRole("button", { name: "Save avatar" }).click();
  await expect(page.getByText(/VYBE has a problem right now/)).toBeVisible();
  await expect(page.getByText("Avatar saved.")).toHaveCount(0);
  await expect(page.getByRole("radio", { name: "Red" })).toBeChecked();

  await saveAvatar(page);
  await page.reload();
  await expect(page.getByRole("radio", { name: "Red" })).toBeChecked();
});

test("keyboard only: start as a guest and save the avatar", async ({ page }) => {
  await page.goto("/");
  const start = page.getByRole("link", { name: "Start as guest" });
  await expect(start).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(start).toBeFocused();
  await page.keyboard.press("Enter");
  const month = page.getByLabel("Month");
  await month.focus();
  await page.keyboard.type("Jun"); // type-ahead chooses the month without opening the list
  await expect(month).toHaveValue("6");
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Year")).toBeFocused();
  await page.keyboard.type("1990");
  await page.keyboard.press("Enter"); // submits the age-gate form
  await expect(page.getByRole("heading", { name: "Create your avatar" })).toBeVisible();

  const bodyA = page.getByRole("radio", { name: "Body A" });
  await bodyA.focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("radio", { name: "Body B" })).toBeChecked();
  await page.getByRole("button", { name: "Save avatar" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByText("Avatar saved.")).toBeVisible();
});
