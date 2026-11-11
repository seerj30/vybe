import type { APIRequestContext } from "@playwright/test";
import { expect, newPlayer } from "./app.ts";
import { me, otherPlayer, verifiedRun } from "./social.ts";
import { test } from "./street-hoops.ts";

// P1 friends and weekly leaderboard (docs/design/p1-friends-leaderboard.md, E13–E15).

const UNSAFE = { "content-type": "application/json", "x-requested-with": "vybe-web" };

async function sendRequest(request: APIRequestContext, handle: string): Promise<string> {
  const response = await request.post("/api/v1/me/friend-requests", { headers: UNSAFE, data: { handle } });
  expect(response.status()).toBe(200);
  return ((await response.json()) as { status: string }).status;
}

/** Makes the two players friends through the API. */
async function befriend(a: APIRequestContext, b: APIRequestContext): Promise<void> {
  expect(await sendRequest(a, (await me(b)).handle)).toBe("pending");
  expect(await sendRequest(b, (await me(a)).handle)).toBe("accepted");
}

test("E13 friends: a player sends a request from a profile, the other accepts it on /friends", async ({ page, browser }) => {
  await newPlayer(page);
  const a = await me(page.request);
  const other = await otherPlayer(browser);
  const b = await me(other.request);

  await page.goto(`/profile/${b.handle}`);
  await page.getByRole("button", { name: `Add ${b.handle} as a friend` }).click();
  await expect(page.getByText(`Friend request sent to ${b.handle}.`)).toBeVisible();
  await expect(page.getByRole("button", { name: "Cancel request" })).toBeVisible();

  await other.goto("/hub");
  await other.getByRole("link", { name: "Friends", exact: true }).click();
  await expect(other).toHaveURL(/\/friends$/);
  const incoming = other.getByRole("list", { name: "Requests for you" });
  await expect(incoming).toContainText(a.handle);
  await incoming.getByRole("button", { name: `Accept ${a.handle}` }).click();
  await expect(other.getByText(`You and ${a.handle} are now friends.`)).toBeVisible();
  await expect(other.getByRole("list", { name: "Your friends" })).toContainText(a.handle);

  await page.goto("/friends");
  const friends = page.getByRole("list", { name: "Your friends" });
  await expect(friends).toContainText(b.handle);
  await expect(friends.getByRole("link", { name: `Challenge ${b.handle}` })).toHaveAttribute("href", `/arcade/street-hoops?to=${b.handle}`);
  await page.goto(`/profile/${b.handle}`);
  await expect(page.getByText(`You and ${b.handle} are friends.`)).toBeVisible();
  await other.context().close();
});

test("E14 friends: a block ends the friendship, and the blocked player's new request does not arrive", async ({ page, browser }) => {
  await newPlayer(page);
  const a = await me(page.request);
  const other = await otherPlayer(browser);
  const b = await me(other.request);
  await befriend(page.request, other.request);

  await page.goto(`/profile/${b.handle}`);
  await expect(page.getByText(`You and ${b.handle} are friends.`)).toBeVisible();
  await page.getByRole("button", { name: `Block ${b.handle}` }).click();
  await page.getByRole("button", { name: "Yes, block" }).click();
  await expect(page.getByText(`You blocked ${b.handle}.`, { exact: true })).toBeVisible();

  await page.goto("/friends");
  await expect(page.getByText("No friends yet.")).toBeVisible();
  await other.goto("/friends");
  await expect(other.getByText("No friends yet.")).toBeVisible();

  // The blocked player sees a normal pending request; the blocker never sees it.
  expect(await sendRequest(other.request, a.handle)).toBe("pending");
  await other.reload();
  await expect(other.getByRole("list", { name: "Sent requests" })).toContainText(a.handle);
  await page.reload();
  await expect(page.getByText("No friends yet.")).toBeVisible();
  await expect(page.getByRole("list", { name: "Requests for you" })).toHaveCount(0);
  await other.context().close();
});

test("E15 leaderboard: a new verified run shows with my rank, also on the friends board", async ({ page, browser }) => {
  await newPlayer(page);
  const a = await me(page.request);
  const other = await otherPlayer(browser);
  const b = await me(other.request);
  await befriend(page.request, other.request);

  await page.goto("/hub");
  await page.getByRole("link", { name: "Weekly leaderboard" }).click();
  await expect(page.getByRole("heading", { name: "Leaderboard", level: 1 })).toBeVisible();
  await expect(page.getByTestId("my-rank")).toContainText("You have no score this week.");

  await verifiedRun(other.request);
  await verifiedRun(page.request);
  await page.reload();
  await expect(page.getByTestId("my-rank")).toContainText(/Your rank: #\d+ · \d+ points/);
  await expect(page.getByRole("row", { name: new RegExp(`${a.handle} \\(you\\)`) })).toBeVisible();

  await page.getByRole("link", { name: "Friends", exact: true }).click();
  await expect(page).toHaveURL(/scope=friends/);
  const rows = page.getByRole("table").getByRole("row");
  await expect(rows).toHaveCount(3); // header + two players
  await expect(page.getByRole("table")).toContainText(b.handle);
  await expect(page.getByTestId("my-rank")).toContainText(/Your rank: #[12] · \d+ points/);
  await other.context().close();
});
