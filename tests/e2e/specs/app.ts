import { expect, type Locator, type Page } from "@playwright/test";

export { expect };

/** An adult year of birth for the age gate (spec §17 decision 1). */
export const ADULT_BIRTH_YEAR = 1990;

/** The `createGuest` body of an adult, for API calls in specs. */
export const ADULT_GUEST = { birthYear: ADULT_BIRTH_YEAR };

/** Fills the age gate on `/play` and starts the guest. */
export async function passAgeGate(page: Page, year = ADULT_BIRTH_YEAR): Promise<void> {
  await page.getByLabel("Year of birth").fill(String(year));
  await page.getByRole("button", { name: "Start as guest" }).click();
}

/** Fills the age-gate dialog that opens before an invite or a sign-in creates a guest. */
export async function passAgeGateDialog(page: Page, year = ADULT_BIRTH_YEAR): Promise<void> {
  const dialog = page.getByRole("dialog", { name: "Before you play" });
  await dialog.getByLabel("Year of birth").fill(String(year));
  await dialog.getByRole("button", { name: "Continue" }).click();
  await expect(dialog).toHaveCount(0);
}

/** Landing → onboarding → age gate → new guest; ends on the avatar creator. Each call is a new player. */
export async function startAsGuest(page: Page): Promise<void> {
  await page.goto("/");
  await page.getByRole("link", { name: "Start as guest" }).click();
  await passAgeGate(page);
  await expect(page.getByRole("heading", { name: "Create your avatar" })).toBeVisible();
}

/** Saves the avatar shown in the creator. */
export async function saveAvatar(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Save avatar" }).click();
  await expect(page.getByText("Avatar saved.")).toBeVisible();
}

/** A new guest with a saved avatar, on the hub. */
export async function newPlayer(page: Page): Promise<void> {
  await startAsGuest(page);
  await saveAvatar(page);
  await page.getByRole("link", { name: "Continue to the hub" }).click();
  await expect(page.getByRole("heading", { name: "Hub" })).toBeVisible();
}

export function walletBalance(page: Page): Locator {
  return page.getByTestId("wallet-balance");
}

/** Opens Street Hoops on an API session (needs a player). */
export async function openOnlineStreetHoops(page: Page): Promise<void> {
  await page.goto("/arcade/street-hoops");
  await expect(page.getByRole("button", { name: "Start 60 s run" })).toBeEnabled();
}

/** Shoots `count` times with the keyboard, each release more than minGapMs (650 ms) after the last. */
export async function shoot(page: Page, count: number): Promise<void> {
  for (let i = 0; i < count; i++) {
    await page.keyboard.down("Space");
    await page.waitForTimeout(300);
    await page.keyboard.up("Space");
    await page.waitForTimeout(500);
  }
}

/** The game clock runs on the server: a run ends after 60 s of real time, then the client submits. */
export const RUN_TIMEOUT = { timeout: 90_000 };

/** Reads the "+N" value of a term in the run summary. */
export async function summaryValue(page: Page, term: string): Promise<number> {
  const text = await page.locator(".summary div", { has: page.getByRole("term").filter({ hasText: term }) }).getByRole("definition").textContent();
  return Number((text ?? "").replace("+", ""));
}
