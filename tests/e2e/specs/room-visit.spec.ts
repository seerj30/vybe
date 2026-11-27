import { type APIRequestContext, type Browser, type Page, test } from "@playwright/test";
import { expect, newPlayer } from "./app.ts";
import { me } from "./social.ts";

/**
 * P2 live friend room visits (docs/design/p2-room-visits.md) against the real hub: the owner opens the room from
 * My Room, friends see "Visit" in the friend list and enter, the owner removes one and closes the room.
 */

const UNSAFE = { "content-type": "application/json", "x-requested-with": "vybe-web" };
const space = (page: Page) => page.locator("vybe-plaza");
const MOBILE = { viewport: { width: 375, height: 812 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true };
const SHOTS = process.env["ROOM_SHOTS"];

async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS === undefined) return;
  await expect(space(page)).toHaveAttribute("data-mode", "3d", { timeout: 30_000 });
  await page.waitForTimeout(1_500); // avatars and room files load
  await page.screenshot({ path: `${SHOTS}/${name}.png` });
}

async function guest(browser: Browser, options = {}): Promise<{ page: Page; handle: string }> {
  const context = await browser.newContext(options);
  const page = await context.newPage();
  await newPlayer(page);
  return { page, handle: (await me(page.request)).handle };
}

/** Makes two players friends through the API. */
async function befriend(a: APIRequestContext, b: APIRequestContext): Promise<void> {
  const ask = await a.post("/api/v1/me/friend-requests", { headers: UNSAFE, data: { handle: (await me(b)).handle } });
  expect(ask.status()).toBe(200);
  const accept = await b.post(`/api/v1/me/friend-requests/${(await me(a)).handle}/accept`, { headers: UNSAFE });
  expect(accept.status()).toBe(200);
}

async function openRoom(page: Page): Promise<void> {
  await page.goto("/room");
  await page.getByTestId("open-room").click();
  await expect(page).toHaveURL(/\/room\/live$/);
  await expect(space(page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
  await expect(space(page)).toHaveAttribute("data-space", "room-host");
}

test.describe("room visits (real hub)", () => {
  test.describe.configure({ mode: "default" });

  test("room visits: friends see the open room, enter, and leave when the owner removes them or closes it", async ({ browser }) => {
    test.setTimeout(150_000); // three new players and the room round trips
    const owner = await guest(browser);
    const friend = await guest(browser);
    const second = await guest(browser);
    try {
      await befriend(owner.page.request, friend.page.request);
      await befriend(owner.page.request, second.page.request);

      // Closed: the friend list shows no visit.
      await friend.page.goto("/friends");
      await expect(friend.page.getByRole("list", { name: "Your friends" })).toContainText(owner.handle);
      await expect(friend.page.getByTestId("visit-room")).toHaveCount(0);

      await openRoom(owner.page);
      await expect(owner.page.getByTestId("instance")).toHaveText("Your room");
      await expect(owner.page.locator("input, textarea, [contenteditable]")).toHaveCount(0); // no free text (P2-3)

      // Open: the friend sees "Open room" and enters with Visit; the second friend opens the link.
      await friend.page.reload();
      await friend.page.getByRole("link", { name: `Visit the room of ${owner.handle}` }).click();
      await expect(friend.page).toHaveURL(new RegExp(`/visit/${owner.handle}$`));
      await expect(space(friend.page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
      await expect(friend.page.getByTestId("instance")).toHaveText(`${owner.handle}'s room`);
      await second.page.goto(`/visit/${owner.handle}`);
      await expect(space(second.page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });

      // Everyone is in one room: the owner's list has the host tag and Remove on each visitor.
      await expect(owner.page.getByTestId("people-button")).toContainText("3 / 8");
      await shot(owner.page, "room-host-desktop");
      await shot(friend.page, "room-visit-desktop");
      await owner.page.getByTestId("people-button").click();
      const list = owner.page.getByTestId("member-list");
      await expect(list.getByRole("listitem")).toHaveCount(3);
      await expect(list.getByTestId("host-tag")).toHaveCount(1);
      await expect(list.getByTestId("remove-member")).toHaveCount(2);
      await expect(list.getByTestId("block-member")).toHaveCount(2);
      await owner.page.getByTestId("people-button").click();
      await friend.page.getByTestId("people-button").click();
      await expect(friend.page.getByTestId("member-list").getByTestId("remove-member")).toHaveCount(0);
      await friend.page.getByTestId("people-button").click();

      // Quick-chat arrives as in the plaza.
      await friend.page.getByRole("button", { name: "Chat", exact: true }).click();
      await friend.page.getByRole("dialog", { name: "Quick chat" }).getByRole("button", { name: "Hi!" }).click();
      await expect(owner.page.getByRole("log", { name: "Room events" })).toContainText(`${friend.handle}: Hi!`);

      // The owner removes the second friend: a clear end state with a way to the plaza.
      await owner.page.getByTestId("people-button").click();
      const row = list.getByRole("listitem").filter({ hasText: second.handle });
      await row.getByTestId("remove-member").click();
      await row.getByTestId("confirm-remove").click();
      await expect(second.page.getByTestId("status")).toContainText("You are no longer in this room.");
      await expect(second.page.getByTestId("go-to-plaza")).toBeVisible();
      await expect(list.getByRole("listitem")).toHaveCount(2);

      // The owner closes the room: the friend sees "The room closed"; the owner is back in My Room.
      await owner.page.getByTestId("close-room").click();
      await expect(owner.page).toHaveURL(/\/room$/);
      await expect(friend.page.getByTestId("status")).toContainText("The room closed.");
      await friend.page.getByTestId("go-to-plaza").click();
      await expect(friend.page).toHaveURL(/\/plaza$/);
      await friend.page.goto("/friends");
      await expect(friend.page.getByRole("list", { name: "Your friends" })).toContainText(owner.handle);
      await expect(friend.page.getByTestId("visit-room")).toHaveCount(0);
    } finally {
      for (const player of [owner, friend, second]) await player.page.context().close();
    }
  });

  test("room visits on a phone (375 px): the owner's live room fits, with the joystick and Close", async ({ browser }) => {
    test.setTimeout(90_000);
    const owner = await guest(browser, MOBILE);
    try {
      await openRoom(owner.page);
      await expect(space(owner.page)).toHaveAttribute("data-mode", "3d", { timeout: 30_000 });
      await expect(owner.page.getByTestId("joystick")).toBeVisible();
      await expect(owner.page.getByTestId("close-room")).toBeVisible();
      await expect(owner.page.getByTestId("people-button")).toContainText("1 / 8");
      await shot(owner.page, "room-host-phone");
      const overflow = await owner.page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
      // A stranger cannot enter: the answer of a closed room.
      const stranger = await guest(browser, MOBILE);
      try {
        await stranger.page.goto(`/visit/${owner.handle}`);
        await expect(stranger.page.getByTestId("status")).toContainText("This room is not open for you now.", { timeout: 20_000 });
        await expect(stranger.page.getByTestId("go-to-plaza")).toBeVisible();
      } finally {
        await stranger.page.context().close();
      }
    } finally {
      await owner.page.context().close();
    }
  });
});
