import type { Locator, Page } from "@playwright/test";
import { expect } from "./app.ts";

/** My Room helpers (room.spec.ts, touch.spec.ts). */
export function roomViewport(page: Page): Locator {
  return page.locator("vybe-room-viewport");
}

export function placedList(page: Page): Locator {
  return page.getByRole("region", { name: /^In your room/ });
}

/** Buys a room item through the API (the shop flow is covered once, in room.spec.ts). */
export async function buyRoomItem(page: Page, sku: string): Promise<void> {
  const catalog = await (await page.request.get("/api/v1/catalog/items?category=room&limit=100")).json();
  const item = catalog.items.find((i: { sku: string }) => i.sku === sku);
  const response = await page.request.post("/api/v1/purchases", {
    data: { itemId: item.id },
    headers: { "X-Requested-With": "vybe-web", "Idempotency-Key": crypto.randomUUID() },
  });
  expect(response.status()).toBe(200);
}
