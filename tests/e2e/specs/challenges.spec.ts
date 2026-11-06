import { expect, newPlayer, openOnlineStreetHoops, RUN_TIMEOUT, shoot, summaryValue } from "./app.ts";
import { acceptStatus, completeAsRecipient, createChallenge, expireInvite, me, otherPlayer, tokenOf, verifiedRun } from "./social.ts";
import { powerMeter, startRun, test } from "./street-hoops.ts";

const VERIFIED = "Run verified by VYBE";

test("E04: creator challenge → share link → second player accepts → verified run → result", async ({ page, browser }) => {
  test.setTimeout(300_000);
  await newPlayer(page);
  const creator = await me(page.request);

  // The creator plays a verified run and makes a challenge from it on the gameplay screen.
  await openOnlineStreetHoops(page);
  await startRun(page);
  await shoot(page, 6);
  await expect(page.getByText(VERIFIED)).toBeVisible(RUN_TIMEOUT);
  const creatorScore = await summaryValue(page, "Score");
  await page.getByRole("link", { name: "Challenge a friend with this run" }).click();
  await expect(page.getByRole("heading", { name: "Your challenge is ready" })).toBeVisible();
  const shareUrl = await page.getByRole("textbox", { name: "Invite link" }).inputValue();
  const token = tokenOf(shareUrl);
  const challenges = page.getByRole("list", { name: "My challenges" });
  await expect(challenges.getByRole("listitem").first()).toContainText("Waiting for a player");

  // A second player opens the link in another browser, without a guest yet.
  const recipientContext = await browser.newContext();
  const recipient = await recipientContext.newPage();
  await recipient.goto(`/challenge/${token}`);
  await expect(recipient.getByTestId("creator-score")).toHaveText(String(creatorScore));
  await recipient.getByRole("button", { name: "Play as a guest and accept" }).click();
  await recipient.getByRole("link", { name: "Play your challenge run" }).click();

  await expect(recipient.getByRole("button", { name: "Start challenge run" })).toBeEnabled();
  await recipient.getByRole("button", { name: "Start challenge run" }).click();
  await expect(powerMeter(recipient)).toBeVisible();
  await shoot(recipient, 6);
  await expect(recipient.getByText(VERIFIED)).toBeVisible(RUN_TIMEOUT);
  const recipientScore = await summaryValue(recipient, "Score");
  // One run per challenge: no "Play again".
  await expect(recipient.getByRole("button", { name: "Play again" })).toHaveCount(0);
  await recipient.getByRole("link", { name: "See the challenge result" }).click();

  const expected =
    recipientScore > creatorScore
      ? { recipient: "You won", creator: "You lost" }
      : recipientScore < creatorScore
        ? { recipient: "You lost", creator: "You won" }
        : { recipient: "Tie", creator: "Tie" };
  const received = recipient.getByRole("list", { name: "My challenges" }).getByRole("listitem").first();
  await expect(received).toContainText(`Challenge from ${creator.handle}`);
  await expect(received).toContainText("Finished");
  await expect(received).toContainText(expected.recipient);
  await expect(received.getByRole("link", { name: /^Rematch/ })).toBeVisible();

  // The creator sees the same result.
  await page.goto("/challenges");
  const sent = page.getByRole("list", { name: "My challenges" }).getByRole("listitem").first();
  await expect(sent).toContainText("Finished");
  await expect(sent).toContainText(expected.creator);
  await expect(sent).toContainText(String(recipientScore));
  await recipientContext.close();
});

test("E07: an expired invite and a self-accept are rejected; a redeemed link shows who cannot play", async ({ page, browser }) => {
  await newPlayer(page);
  const own = await createChallenge(page.request);
  const expired = await createChallenge(page.request);
  await expireInvite(expired.challengeId);

  // Self-accept: the creator sees their own challenge, and the API refuses the accept.
  await page.goto(`/challenge/${own.token}`);
  await expect(page.getByText("This is your challenge.")).toBeVisible();
  await expect(page.getByRole("button", { name: /accept/i })).toHaveCount(0);
  expect(await acceptStatus(page.request, own.token)).toEqual({ status: 403, code: "self_accept" });

  const second = await otherPlayer(browser);
  await second.goto(`/challenge/${expired.token}`);
  await expect(second.getByText("This challenge has expired.")).toBeVisible();
  await expect(second.getByRole("button", { name: /accept/i })).toHaveCount(0);
  expect(await acceptStatus(second.request, expired.token)).toEqual({ status: 410, code: "challenge_expired" });

  // The link works once: after the second player accepts, a third player sees it as redeemed.
  await second.goto(`/challenge/${own.token}`);
  await second.getByRole("button", { name: "Accept challenge" }).click();
  await expect(second.getByRole("link", { name: "Play your challenge run" })).toBeVisible();
  const third = await otherPlayer(browser);
  await third.goto(`/challenge/${own.token}`);
  await expect(third.getByText("Another player already accepted this challenge.")).toBeVisible();

  // The creator's list shows the expired invite as expired.
  await page.goto("/challenges");
  await expect(page.getByRole("list", { name: "My challenges" }).getByRole("listitem").filter({ hasText: "Expired" })).toHaveCount(1);
  await second.context().close();
  await third.context().close();
});

test("an unknown invite link shows the \"not available\" state", async ({ page }) => {
  await page.goto(`/challenge/${"A".repeat(43)}`);
  await expect(page.getByText("This challenge is not available.")).toBeVisible();
});

test("a profile challenge goes to that player only, who opens it from their list", async ({ page, browser }) => {
  await newPlayer(page);
  const recipient = await otherPlayer(browser);
  const { handle } = await me(recipient.request);
  await createChallenge(page.request, handle);

  await recipient.goto("/challenges");
  await recipient.getByRole("list", { name: "My challenges" }).getByRole("link", { name: "Open invite" }).click();
  await recipient.getByRole("button", { name: "Accept challenge" }).click();
  await expect(recipient.getByRole("link", { name: "Play your challenge run" })).toBeVisible();
  await recipient.context().close();
});

test("challenges list: empty state for a new player", async ({ page }) => {
  await newPlayer(page);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Challenges" }).click();
  await expect(page.getByText("You have no challenges yet.")).toBeVisible();
});

test("rematch: one per participant; the run can still become a new challenge", async ({ page, browser }) => {
  await newPlayer(page);
  const opponent = await otherPlayer(browser);
  const original = await createChallenge(page.request);
  await completeAsRecipient(opponent.request, original);

  // The creator sends a rematch from a new verified run; only the opponent can take it.
  const rematchRun = await verifiedRun(page.request);
  await page.goto(`/challenges?session=${rematchRun}&rematchOf=${original.challengeId}`);
  await expect(page.getByRole("heading", { name: "Your challenge is ready" })).toBeVisible();
  await expect(page.getByText("Only your opponent can accept the rematch.")).toBeVisible();
  const finished = page.getByRole("list", { name: "My challenges" }).getByRole("listitem").filter({ hasText: "Finished" });
  await expect(finished).toHaveCount(1);
  await expect(finished.getByRole("link", { name: /^Rematch/ })).toHaveCount(0);

  await opponent.goto("/challenges");
  await opponent.getByRole("list", { name: "My challenges" }).getByRole("link", { name: "Open invite" }).click();
  await expect(opponent.getByRole("button", { name: "Accept challenge" })).toBeVisible();

  // A second rematch fails, but the verified run is not lost.
  const secondRun = await verifiedRun(page.request);
  await page.goto(`/challenges?session=${secondRun}&rematchOf=${original.challengeId}`);
  await expect(page.getByText("You already sent a rematch for this challenge.")).toBeVisible();
  await page.getByRole("link", { name: "Send this run as a new challenge" }).click();
  await expect(page.getByRole("heading", { name: "Your challenge is ready" })).toBeVisible();
  await opponent.context().close();
});
