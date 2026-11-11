import { expect, newPlayer } from "./app.ts";
import { backdateRunStart } from "./shots.ts";
import { me, otherPlayer } from "./social.ts";
import { test } from "./street-hoops.ts";
import { openOnlineStreetJuggle, rewriteTouches, startRun, touch } from "./street-juggle.ts";

// P1 achievements E2E cases (docs/design/p1-achievements.md).

test("E20: a first verified run unlocks an achievement: toast, /achievements and the profile badge", async ({ page, browser }) => {
  await newPlayer(page);
  await rewriteTouches(page, "perfect");
  await page.clock.install();
  await openOnlineStreetJuggle(page);
  const sessionId = await startRun(page);
  // Test data: the server sees a full-length run without 60 s of waiting (the production check is unchanged).
  await backdateRunStart(sessionId, 60_000);
  await touch(page, 5);
  await page.clock.fastForward("01:01");

  await expect(page.getByText("Run verified by VYBE")).toBeVisible();
  const toast = page.getByTestId("achievement-toast");
  await expect(toast).toContainText("Achievement unlocked: First Touch");
  await expect(toast).toContainText("+25 XP");
  await toast.getByRole("link", { name: "See your achievements" }).click();

  await expect(page.getByRole("heading", { name: "Achievements", level: 1 })).toBeVisible();
  await expect(page.getByTestId("achievement-summary")).toContainText(/\d+ of 29 unlocked/);
  const firstTouch = page.locator('[data-achievement="juggle_first_run"]');
  await expect(firstTouch).toHaveAttribute("data-state", "unlocked");
  await expect(firstTouch).toContainText(/Unlocked .* \+25 XP/);
  const runs10 = page.locator('[data-achievement="runs_10"]');
  await expect(runs10).toHaveAttribute("data-state", "progress");
  await expect(runs10.getByRole("progressbar", { name: "Warming Up progress" })).toHaveAttribute("aria-valuenow", "1");

  // Filters: "Locked" hides the unlocked one.
  await page.getByRole("button", { name: "Locked", exact: true }).click();
  await expect(firstTouch).toBeHidden();
  await expect(page.getByRole("button", { name: "Locked", exact: true })).toHaveAttribute("aria-pressed", "true");
  await page.getByRole("button", { name: "All", exact: true }).click();

  // The own profile shows the badge; another signed-in player sees it too.
  await page.getByRole("link", { name: "See your badges on your profile" }).click();
  await expect(page.getByTestId("badges").locator('[data-badge="juggle_first_run"]')).toContainText("First Touch");

  const { handle } = await me(page.request);
  const other = await otherPlayer(browser);
  await other.goto(`/profile/${handle}`);
  await expect(other.getByTestId("badges")).toContainText("First Touch");

  // A private profile hides the badges (and the whole profile) from other players (decision 6).
  const privacy = await page.request.patch("/api/v1/me/privacy", {
    headers: { "content-type": "application/json", "x-requested-with": "vybe-web" },
    data: { profileVisibility: "private" },
  });
  expect(privacy.status()).toBe(200);
  await other.reload();
  await expect(other.getByText("This profile is not available.")).toBeVisible();
  await expect(other.getByTestId("badges")).toHaveCount(0);
  await other.context().close();
});

test("the hub links to /achievements; a new player has no badges yet", async ({ page }) => {
  await newPlayer(page);
  await page.getByRole("link", { name: "Achievements" }).click();
  await expect(page.getByTestId("achievement-summary")).toContainText("0 of 29 unlocked");
  await expect(page.locator('[data-achievement="hoops_first_run"]')).toHaveAttribute("data-state", "locked");
  await page.getByRole("link", { name: "See your badges on your profile" }).click();
  await expect(page.getByText("No badges yet. Finish a run to unlock your first one.")).toBeVisible();
});
