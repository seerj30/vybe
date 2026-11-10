import { expect, newPlayer, walletBalance } from "./app.ts";
import { completeQuest, createChallenge, loseFirstAnswer, me, otherPlayer, removeOwnedItem, reportCount } from "./social.ts";
import { test } from "./street-hoops.ts";

test("quest claim: a completed daily quest pays once, then shows as claimed", async ({ page }) => {
  await newPlayer(page);
  await completeQuest(page, "play_3");
  await page.reload();

  const quest = page.locator('[data-quest="play_3"]');
  await expect(quest).toContainText("3 / 3");
  await expect(page.locator('[data-quest="score_1500"]')).toContainText("0 / 1");
  await quest.getByRole("button", { name: "Claim Finish 3 runs" }).click();
  await expect(page.getByText("Quest claimed: +100 V-Coins, +60 XP.")).toBeVisible();
  await expect(quest.getByText("Claimed")).toBeVisible();
  await expect(quest.getByRole("button")).toHaveCount(0);
  await expect(walletBalance(page)).toHaveText("100");
});

test("block: the blocked player's invite becomes unavailable; the block can be removed", async ({ page, browser }) => {
  await newPlayer(page);
  const creator = await me(page.request);
  const { token } = await createChallenge(page.request);

  const blocker = await otherPlayer(browser);
  await blocker.goto(`/profile/${creator.handle}`);
  await expect(blocker.getByRole("heading", { name: creator.handle })).toBeVisible();
  await blocker.getByRole("button", { name: `Block ${creator.handle}` }).click();
  await blocker.getByRole("button", { name: "Yes, block" }).click();
  await expect(blocker.getByText(`You blocked ${creator.handle}.`, { exact: true })).toBeVisible();
  await expect(blocker.getByRole("link", { name: `Challenge ${creator.handle}` })).toHaveCount(0);

  await blocker.goto(`/challenge/${token}`);
  await expect(blocker.getByText("This challenge is not available.")).toBeVisible();

  // The block survives a reload and can be removed.
  await blocker.goto(`/profile/${creator.handle}`);
  await blocker.getByRole("button", { name: `Unblock ${creator.handle}` }).click();
  await expect(blocker.getByRole("link", { name: `Challenge ${creator.handle}` })).toBeVisible();
  await blocker.context().close();
});

test("report: a player reports another player from the profile", async ({ page, browser }) => {
  await newPlayer(page);
  const target = await otherPlayer(browser);
  const { handle } = await me(target.request);

  await page.goto(`/profile/${handle}`);
  await page.getByRole("button", { name: `Report ${handle}` }).click();
  await page.getByRole("radio", { name: "Cheating" }).check();
  await page.getByRole("textbox", { name: "Details (optional)" }).fill("Scores look automated.");
  await page.getByRole("button", { name: "Send report" }).click();
  await expect(page.getByText("Thank you. VYBE moderators will review your report.")).toBeVisible();
  expect(await reportCount(page, target)).toBe(1);

  // The own profile has no safety actions; an unknown handle is unavailable.
  const self = await me(page.request);
  await page.goto(`/profile/${self.handle}`);
  await expect(page.getByText("This is your profile.")).toBeVisible();
  await expect(page.getByRole("button", { name: /^Report/ })).toHaveCount(0);
  await page.goto("/profile/nobody_here_0");
  await expect(page.getByText("This profile is not available.")).toBeVisible();
  await target.context().close();
});

test("saved outfit: save, wear again, and fail gracefully when an item is gone", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/wardrobe");
  await page.getByRole("textbox", { name: "Outfit name" }).fill("Starter look");
  await page.getByRole("button", { name: "Save current outfit" }).click();
  await expect(page.getByText('Outfit "Starter look" saved.')).toBeVisible();

  await page.getByRole("button", { name: "Take off Starter Tee" }).click();
  await expect(page.getByRole("button", { name: "Wear Starter Tee" })).toBeVisible();
  await page.getByRole("button", { name: "Wear outfit Starter look" }).click();
  await expect(page.getByText('You now wear the outfit "Starter look".')).toBeVisible();
  await expect(page.getByRole("button", { name: "Take off Starter Tee" })).toBeVisible();

  // The player loses an item of the outfit: the outfit names it and cannot be worn; nothing changes.
  await page.getByRole("button", { name: "Take off Starter Kicks" }).click();
  await expect(page.getByRole("button", { name: "Wear Starter Kicks" })).toBeVisible();
  await removeOwnedItem(page, "Starter Kicks");
  await page.reload();
  const outfit = page.getByRole("listitem", { name: "Starter look" });
  await expect(outfit).toContainText("You cannot wear this outfit now. Not available: 1 item you no longer own.");
  await expect(outfit.getByRole("button", { name: "Wear outfit Starter look" })).toBeDisabled();
  const outfitId = ((await (await page.request.get("/api/v1/me/outfits")).json()) as { outfits: { id: string }[] }).outfits[0]?.id;
  const avatar = (await (await page.request.get("/api/v1/me/avatar")).json()) as { revision: number };
  const apply = await page.request.post(`/api/v1/me/outfits/${outfitId}/apply`, {
    headers: { "content-type": "application/json", "x-requested-with": "vybe-web" },
    data: { expectedAvatarRevision: avatar.revision },
  });
  expect(apply.status()).toBe(422);
  await expect(page.getByRole("button", { name: "Take off Starter Tee" })).toBeVisible();

  await outfit.getByRole("button", { name: "Delete outfit Starter look" }).click();
  await expect(page.getByText("No saved outfits.")).toBeVisible();
});

test("quest claim: a lost answer is retried with the same Idempotency-Key and pays once", async ({ page }) => {
  await newPlayer(page);
  await completeQuest(page, "score_1500");
  const claims = await loseFirstAnswer(page, "**/api/v1/me/quests/*/claim");
  await page.reload();
  const quest = page.locator('[data-quest="score_1500"]');
  await quest.getByRole("button", { name: /^Claim/ }).click();
  await expect(page.getByText(/No connection to VYBE/)).toBeVisible();
  await quest.getByRole("button", { name: /^Claim/ }).click();
  await expect(page.getByText("Quest claimed: +100 V-Coins, +60 XP.")).toBeVisible();
  expect(claims).toHaveLength(2);
  expect(claims[1]).toBe(claims[0]);
  await expect(walletBalance(page)).toHaveText("100");
});

test("saved outfit: an item lost after the list loaded → Wear fails with a message and changes nothing", async ({ page }) => {
  await newPlayer(page);
  await page.goto("/wardrobe");
  await page.getByRole("textbox", { name: "Outfit name" }).fill("Full kit");
  await page.getByRole("button", { name: "Save current outfit" }).click();
  await expect(page.getByText('Outfit "Full kit" saved.')).toBeVisible();
  await page.getByRole("button", { name: "Take off Starter Kicks" }).click();
  await expect(page.getByRole("button", { name: "Wear Starter Kicks" })).toBeVisible();

  // The list on screen still says the outfit is valid.
  await removeOwnedItem(page, "Starter Kicks");
  await page.getByRole("button", { name: "Wear outfit Full kit" }).click();
  await expect(page.getByText("Some items in this outfit are not yours any more or do not fit. Nothing changed.")).toBeVisible();
  // The list is read again and now names the problem; what the player wears did not change.
  await expect(page.getByRole("listitem", { name: "Full kit" })).toContainText("Not available: 1 item you no longer own.");
  await expect(page.getByRole("button", { name: "Take off Starter Tee" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Take off Starter Kicks" })).toHaveCount(0);
});
