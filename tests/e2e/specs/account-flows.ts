import type { APIRequestContext, APIResponse, Locator, Page } from "@playwright/test";
import type { TestUser } from "../fake-auth/users.ts";
import { expect } from "./app.ts";

/**
 * Helpers for sign-in, linking, privacy and admin specs. The Supabase session lives in memory only
 * (spec §11: no tokens in localStorage), so after a sign-in the specs move with in-app links:
 * `page.goto` reloads the app and signs the user out.
 */

/** Fills and sends the sign-in form inside `scope`. */
export async function fillSignIn(scope: Locator | Page, user: TestUser): Promise<void> {
  const form = scope.getByRole("form", { name: "Sign in" });
  await form.getByLabel("Email").fill(user.email);
  await form.getByLabel("Password").fill(user.password);
  await form.getByRole("button", { name: "Sign in" }).click();
}

/** Confirms the link of this browser's guest (or a new player) to the signed-in account `email`. */
export async function confirmLink(scope: Locator | Page, email: string): Promise<void> {
  await expect(scope.getByTestId("link-email")).toHaveText(email);
  await scope.getByRole("button", { name: "Link this account" }).click();
}

/** Landing → "Sign in" → form. */
export async function signInOnLanding(page: Page, user: TestUser): Promise<void> {
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  await fillSignIn(page.getByRole("region", { name: "Sign in to VYBE" }), user);
}

/** Opens a screen through the main navigation (keeps the in-memory session). */
export async function openNav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name }).click();
}

/** A signed-in admin or player at `/admin`. */
export async function signInOnAdmin(page: Page, user: TestUser): Promise<void> {
  await page.goto("/admin");
  await fillSignIn(page, user);
  await expect(page.getByText(`Signed in as ${user.email}`)).toBeVisible();
}

export function bearer(token: string): Record<string, string> {
  return { Authorization: `Bearer ${token}` };
}

/** Unsafe API call as the guest cookie of `request` (CSRF header, as the web app sends it). */
export function guestPost(request: APIRequestContext, path: string, data: unknown): Promise<APIResponse> {
  return request.post(`/api/v1${path}`, { headers: { "X-Requested-With": "vybe-web" }, data });
}

export async function json<T>(response: APIResponse): Promise<T> {
  expect(response.ok(), `${response.url()} answered ${response.status()}`).toBe(true);
  return (await response.json()) as T;
}
