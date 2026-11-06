import { expect, expectCharging, openStreetHoops, powerMeter, SHOT_RESULT, startButton, startRun, test, TOO_SOON } from "./street-hoops.ts";

test("loads and verifies the ruleset, then enables Start", async ({ page }) => {
  await page.goto("/");
  await expect(startButton(page)).toBeEnabled();
  await expect(startButton(page)).toHaveText("Start 60 s run");
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test("keyboard: hold and release Space records a shot; a quick second release is too soon", async ({ page }) => {
  await openStreetHoops(page);
  await startRun(page);

  // Aim with ArrowLeft while the shot charges.
  await page.keyboard.down("ArrowLeft");
  await page.keyboard.down("Space");
  await expectCharging(page);
  await page.keyboard.up("Space");
  // minGapMs is 650 ms in street-hoops-v1. Release again at once, before any assertion:
  // slow CI renderers (~13 FPS) can spend more than 650 ms in a single expect.
  await page.keyboard.down("Space");
  await page.keyboard.up("Space");
  await page.keyboard.up("ArrowLeft");
  await expect(page.getByText(TOO_SOON)).toBeVisible();
  await expect(powerMeter(page)).toHaveAttribute("aria-valuenow", "0");

  // A new charge clears the notice, and the label shows the first shot again: it was recorded.
  await page.keyboard.down("Space");
  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
  await page.keyboard.up("Space");
});

test("the result shows when the ball reaches the rim, not at release", async ({ page }) => {
  // Fake time starts at T0 and runs with real time until pauseAt. T0 + 1 h is always still in the future,
  // so pauseAt cannot fail on a slow runner (a target read from Date.now() + 50 ms could already be past).
  const t0 = new Date("2026-01-01T00:00:00Z").getTime();
  await page.clock.install({ time: t0 });
  await openStreetHoops(page);
  await expect(page.getByRole("checkbox", { name: "Reduce motion" })).not.toBeChecked();

  // From here, page time runs only when the test moves it, so slow CI frames cannot reach the 700 ms flight time.
  await page.clock.pauseAt(t0 + 60 * 60 * 1000);
  await startButton(page).click();
  await page.clock.runFor(100);
  await expect(powerMeter(page)).toBeVisible();

  await page.keyboard.down("Space");
  await page.clock.runFor(300);
  await expectCharging(page);
  await page.keyboard.up("Space");
  await page.clock.runFor(100);
  // The meter reset proves the view updated after the release; the label still waits for the ball.
  await expect(powerMeter(page)).toHaveAttribute("aria-valuenow", "0");
  await expect(page.getByText(SHOT_RESULT)).toHaveCount(0);

  await page.clock.runFor(700);
  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
});

test("keyboard: Enter also shoots", async ({ page }) => {
  await openStreetHoops(page);
  await startRun(page);

  await page.keyboard.down("Enter");
  await expectCharging(page);
  await page.keyboard.up("Enter");
  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
});

test.describe("reduced motion", () => {
  test.describe("when the OS asks for it", () => {
    test.use({ reducedMotion: "reduce" });

    test("Reduce motion defaults to on", async ({ page }) => {
      await openStreetHoops(page);
      await expect(page.getByRole("checkbox", { name: "Reduce motion" })).toBeChecked();
    });
  });

  test.describe("when the OS does not ask for it", () => {
    test.use({ reducedMotion: "no-preference" });

    test("Reduce motion defaults to off", async ({ page }) => {
      await openStreetHoops(page);
      await expect(page.getByRole("checkbox", { name: "Reduce motion" })).not.toBeChecked();
    });
  });
});

test("the run ends after 60 s and shows the summary", async ({ page }) => {
  await page.clock.install();
  await openStreetHoops(page);
  await startRun(page);

  await page.clock.fastForward("01:01");

  await expect(powerMeter(page)).toHaveCount(0);
  await expect(startButton(page)).toHaveText("Play again");
  const overlay = page.getByRole("region", { name: "Street Hoops" });
  await expect(overlay.getByRole("term")).toHaveText(["Score", "Shots", "Max streak"]);
  await expect(overlay.getByRole("definition")).toHaveText(["0", "0", "0"]);
});
