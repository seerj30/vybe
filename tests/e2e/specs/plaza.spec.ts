import { type Browser, type Page, test } from "@playwright/test";
import { expect, newPlayer } from "./app.ts";
import { me } from "./social.ts";

/**
 * P2 plaza (docs/design/p2-spaces-protocol.md). The first tests use the dev fake hub (`/plaza?fake=1`, dev builds
 * only), so the page is tested without the server hub. The last test uses the real hub (stream V); it skips
 * itself while the API has no ticket endpoint. The page reports its state as data attributes.
 */

const plaza = (page: Page) => page.locator("vybe-plaza");
const SHOTS = process.env["PLAZA_SHOTS"];

async function openFakePlaza(page: Page, query = "bots=15&still=1"): Promise<void> {
  await page.goto(`/plaza?fake=1&${query}`);
  await expect(plaza(page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
}

async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS !== undefined) await page.screenshot({ path: `${SHOTS}/${name}.png` });
}

test("plaza (fake hub): 16 avatars inside the draw-call budget, and the member list", async ({ page }) => {
  await newPlayer(page);
  await page.getByTestId("plaza-link").click();
  await expect(page).toHaveURL(/\/plaza$/);

  await openFakePlaza(page);
  await expect(plaza(page)).toHaveAttribute("data-mode", "3d", { timeout: 30_000 });
  await expect(plaza(page)).toHaveAttribute("data-avatars", "16", { timeout: 30_000 });
  await expect(page.getByTestId("name-tag")).toHaveCount(16);
  // Spec §9: at most 60 draw calls; one merged mesh per avatar keeps 16 avatars at about 18.
  const drawCalls = Number(await plaza(page).getAttribute("data-draw-calls"));
  expect(drawCalls).toBeGreaterThan(0);
  expect(drawCalls).toBeLessThanOrEqual(60);
  await expect(page.getByRole("heading", { name: "Plaza 3" })).toBeVisible();
  await expect(page.getByTestId("people-button")).toContainText("16 / 16");
  // No text input anywhere (P2-3).
  await expect(page.locator("input, textarea, [contenteditable]")).toHaveCount(0);

  await page.getByTestId("people-button").click();
  const list = page.getByTestId("member-list");
  await expect(list.getByRole("listitem")).toHaveCount(16);
  await expect(list.getByRole("link", { name: "Profile, report or block" }).first()).toHaveAttribute("href", /^\/profile\/[a-z0-9_]+$/);
  await expect(list.getByText("Private profile").first()).toBeVisible();
  await page.getByTestId("people-button").click();
  await shot(page, "plaza-desktop");
});

test("plaza (fake hub): quick-chat with its cooldown, stickers, emotes and keyboard movement", async ({ page }) => {
  await newPlayer(page);
  await openFakePlaza(page, "bots=2&still=1");
  await expect(plaza(page)).toHaveAttribute("data-mode", "3d", { timeout: 30_000 });

  await page.getByRole("button", { name: "Chat", exact: true }).click();
  const panel = page.getByRole("dialog", { name: "Quick chat" });
  // One phrase per 2 s (protocol §7): the buttons wait, so the server never drops one. Send and open the panel
  // again in one page task, so a slow test machine cannot pass the 2 s between the two steps.
  const disabledAfterSend = await page.evaluate(async () => {
    const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const button = (name: string) => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find((b) => b.textContent?.trim() === name);
    button("Good game!")!.click();
    await frame();
    button("Chat")!.click();
    await frame();
    return button("Hi!")!.disabled;
  });
  expect(disabledAfterSend).toBe(true);
  await expect(page.getByTestId("bubble").filter({ hasText: "Good game!" })).toBeVisible();
  await expect(page.getByRole("log", { name: "Plaza events" })).toContainText("You: Good game!");
  await expect(panel.getByRole("button", { name: "Hi!" })).toBeEnabled({ timeout: 5_000 });
  await page.keyboard.press("Escape");

  await page.getByRole("button", { name: "Stickers" }).click();
  await page.getByRole("dialog", { name: "Stickers" }).getByRole("button", { name: "Heart" }).click();
  await expect(page.getByRole("log", { name: "Plaza events" })).toContainText("You: Heart sticker");

  await page.getByRole("button", { name: "Emotes" }).click();
  await expect(page.getByRole("dialog", { name: "Emotes" })).toContainText("You have no emotes yet.");

  await page.keyboard.press("Escape");

  // Keyboard movement sends a moving intent; releasing the key sends the stop.
  await page.keyboard.down("KeyW");
  await expect(plaza(page)).toHaveAttribute("data-moving", "true");
  await page.keyboard.up("KeyW");
  await expect(plaza(page)).toHaveAttribute("data-moving", "false");
  await expect(plaza(page)).toHaveAttribute("data-status", "joined");
});

test("plaza (fake hub) on a phone: joystick and stacked buttons at 375 px", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  const page = await context.newPage();
  try {
    await newPlayer(page);
    await openFakePlaza(page, "bots=7&still=1");
    await expect(plaza(page)).toHaveAttribute("data-avatars", "8", { timeout: 30_000 });
    await expect(page.getByTestId("joystick")).toBeVisible();
    // The page never scrolls sideways at 375 px.
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
    await page.getByRole("button", { name: "Chat", exact: true }).click();
    await page.getByRole("dialog", { name: "Quick chat" }).getByRole("button", { name: "Hi!" }).click();
    await expect(page.getByTestId("bubble").filter({ hasText: "Hi!" })).toBeVisible();
    await shot(page, "plaza-375");
  } finally {
    await context.close();
  }
});

test("plaza (fake hub) on a phone: losing the 3D view during a joystick drag stops the avatar", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true });
  const page = await context.newPage();
  try {
    await newPlayer(page);
    await openFakePlaza(page, "bots=1&still=1");
    await expect(plaza(page)).toHaveAttribute("data-mode", "3d", { timeout: 30_000 });
    const box = (await page.getByTestId("joystick").boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2, box.y - 20);
    await expect(plaza(page)).toHaveAttribute("data-moving", "true");
    // The GPU drops the context while the finger is still down: the joystick goes away, no pointerup comes.
    await page.evaluate(() => {
      const gl = document.querySelector<HTMLCanvasElement>("vybe-plaza canvas")!.getContext("webgl2") ?? document.querySelector("canvas")!.getContext("webgl");
      gl!.getExtension("WEBGL_lose_context")!.loseContext();
    });
    await expect(plaza(page)).toHaveAttribute("data-mode", "fallback");
    await expect(plaza(page)).toHaveAttribute("data-moving", "false");
    await page.mouse.up();
  } finally {
    await context.close();
  }
});

test("plaza: a failed load of the connection code shows Try again, not a spinner", async ({ page }) => {
  await newPlayer(page);
  // Fail the lazy chunk with the SignalR client once (found by content: chunk names change with every build).
  let failed = false;
  await page.route(/\.js(\?|$)/, async (route) => {
    const response = await route.fetch();
    const body = await response.text();
    if (!failed && body.includes("skipNegotiation") && body.includes("MessagePackHubProtocol")) {
      failed = true;
      await route.abort("failed");
      return;
    }
    await route.fulfill({ response, body });
  });
  await page.goto("/plaza");
  await expect(page.getByText("The plaza did not load. Check your connection and try again.")).toBeVisible({ timeout: 20_000 });
  expect(failed).toBe(true);
  await expect(plaza(page)).toHaveAttribute("data-status", "failed");
  // Only the loaded connection code asks for a hub ticket: that request proves the second load worked.
  const ticket = page.waitForRequest((request) => request.url().endsWith("/api/v1/spaces/tickets") && request.method() === "POST");
  await page.getByRole("button", { name: "Try again" }).click();
  await ticket;
  await expect(plaza(page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
});

test("plaza (fake hub) without WebGL: members and events as lists", async ({ page }) => {
  await newPlayer(page);
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, type: string, ...rest: unknown[]) {
      if (type.startsWith("webgl")) return null;
      return (original as (...args: unknown[]) => RenderingContext | null).call(this, type, ...rest);
    } as typeof original;
  });
  await openFakePlaza(page, "bots=3&still=1");
  await expect(plaza(page)).toHaveAttribute("data-mode", "fallback", { timeout: 20_000 });
  await expect(page.getByText("3D is not available on this device, so the plaza shows as a list.")).toBeVisible();
  await expect(page.getByTestId("fallback-members").getByRole("listitem")).toHaveCount(4);
  await page.getByRole("button", { name: "Chat", exact: true }).click();
  await page.getByRole("dialog", { name: "Quick chat" }).getByRole("button", { name: "Welcome!" }).click();
  await expect(page.getByTestId("fallback-feed")).toContainText("You: Welcome!");
});

// ------------------------------------------------------------------ real hub (stream V)

async function hubAvailable(page: Page): Promise<boolean> {
  const response = await page.request.post("/api/v1/spaces/tickets", { headers: { "x-requested-with": "vybe-web" } });
  return response.status() !== 404 && response.status() !== 405;
}

async function guestInPlaza(browser: Browser): Promise<{ page: Page; handle: string }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await newPlayer(page);
  const { handle } = await me(page.request);
  await page.goto("/plaza");
  await expect(plaza(page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
  return { page, handle };
}

test("plaza (real hub): two guests see each other, quick-chat arrives, and a block separates them", async ({ page, browser }) => {
  test.setTimeout(120_000); // three new players and a block round trip
  await newPlayer(page);
  test.skip(!(await hubAvailable(page)), "Waits for the stream V hub (POST /api/v1/spaces/tickets).");
  // Each hub test opens its own pair of players; they join the fullest instance, so run one test only.
  const a = await guestInPlaza(browser);
  const b = await guestInPlaza(browser);
  try {
    const instance = await plaza(a.page).getAttribute("data-instance");
    await expect(plaza(b.page)).toHaveAttribute("data-instance", instance!);
    // Both avatars render on both screens (a name tag shows from the first snapshot entry). Other tests' players
    // (and their 30 s slot reservations) can share the instance, so check the two names, not a count.
    const tag = (page: Page, name: string) => page.getByTestId("name-tag").filter({ hasText: new RegExp(`^\\s*${name}\\s*$`) });
    for (const [page, other] of [
      [a.page, b.handle],
      [b.page, a.handle],
    ] as const) {
      await expect(tag(page, "You")).toHaveCount(1, { timeout: 20_000 });
      await expect(tag(page, other)).toHaveCount(1, { timeout: 20_000 });
    }
    await a.page.getByTestId("people-button").click();
    await expect(a.page.getByTestId("member-list")).toContainText(b.handle);
    await b.page.getByTestId("people-button").click();
    await expect(b.page.getByTestId("member-list")).toContainText(a.handle);
    await b.page.getByTestId("people-button").click();

    await a.page.getByRole("button", { name: "Chat", exact: true }).click();
    await a.page.getByRole("dialog", { name: "Quick chat" }).getByRole("button", { name: "Hi!" }).click();
    await expect(b.page.getByRole("log", { name: "Plaza events" })).toContainText(`${a.handle}: Hi!`);

    // A opens B's profile from the member list and blocks B; the hub moves A (the blocker) to another instance.
    await a.page.getByTestId("people-button").click();
    await a.page.getByTestId("member-list").getByRole("listitem").filter({ hasText: b.handle }).getByRole("link").click();
    await expect(a.page).toHaveURL(new RegExp(`/profile/${b.handle}$`));
    await a.page.getByRole("button", { name: `Block ${b.handle}` }).click();
    await a.page.getByRole("button", { name: "Yes, block" }).click();
    await expect(a.page.getByText(`You blocked ${b.handle}.`, { exact: true })).toBeVisible();

    // A left the plaza for the profile; B sees A leave. A back in the plaza: never with B again.
    await b.page.getByTestId("people-button").click();
    await expect(b.page.getByTestId("member-list")).not.toContainText(a.handle, { timeout: 40_000 });
    await a.page.goto("/plaza");
    await expect(plaza(a.page)).toHaveAttribute("data-status", "joined", { timeout: 20_000 });
    await expect(plaza(a.page)).not.toHaveAttribute("data-instance", instance!);
    await a.page.getByTestId("people-button").click();
    await expect(a.page.getByTestId("member-list")).not.toContainText(b.handle);
  } finally {
    await a.page.context().close();
    await b.page.context().close();
  }
});
