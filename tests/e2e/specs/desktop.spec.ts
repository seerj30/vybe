import { expect, test } from "@playwright/test";
import { expectCharging, openStreetHoops, powerMeter, SHOT_RESULT, startButton, startRun, TOO_SOON } from "./street-hoops.ts";

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
  await page.keyboard.up("ArrowLeft");
  await expect(page.getByText(SHOT_RESULT)).toBeVisible();
  await expect(powerMeter(page)).toHaveAttribute("aria-valuenow", "0");

  // minGapMs is 650 ms in street-hoops-v1; this release follows at once.
  await page.keyboard.down("Space");
  await page.keyboard.up("Space");
  await expect(page.getByText(TOO_SOON)).toBeVisible();
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
