import { expect, type Locator, type Page } from "@playwright/test";

export { expect };

/** Landing → onboarding → new guest; ends on the avatar creator. Each call is a new player. */
export async function startAsGuest(page: Page): Promise<void> {
  await page.goto("/");
  await page.getByRole("link", { name: "Start as guest" }).click();
  await page.getByRole("button", { name: "Start as guest" }).click();
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
