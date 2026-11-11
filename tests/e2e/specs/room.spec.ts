import { expect, newPlayer } from "./app.ts";
import { grantCoins } from "./db.ts";
import { buyRoomItem, placedList, roomViewport } from "./room.ts";
import { test } from "./street-hoops.ts";

/**
 * My Room (docs/design/p1-my-room.md): buy a room item in the shop, place it, save, and see it again after a
 * reload and on the profile. A room file that does not load shows the 2D plan or a box with a notice, and the
 * editor still works. The viewport reports its state and the shown skus as data attributes.
 */

const SHELL_FAILED = "The 3D room could not load, so a plan shows instead.";
const ITEMS_FAILED = "Some room items could not load, so boxes show instead. Your room did not change.";

const viewport = roomViewport;
const placed = placedList;
const buy = buyRoomItem;

test("buy a furniture item, place it, save, and a reload and the profile show it", async ({ page }) => {
  await newPlayer(page);
  await grantCoins(page, 1_000);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Shop" }).click();
  await page.getByLabel("Show").selectOption({ label: "All room items" });
  const beanbag = page.getByRole("listitem", { name: "Beanbag" });
  await beanbag.getByRole("button", { name: "Buy" }).click();
  await beanbag.getByRole("button", { name: "Confirm purchase" }).click();
  await beanbag.getByRole("link", { name: "Place it" }).click();

  await expect(page).toHaveURL(/\/room$/);
  await expect(viewport(page)).toHaveAttribute("data-state", "3d", { timeout: 20_000 });
  await page.getByRole("button", { name: "Place Beanbag" }).click();
  await expect(placed(page).getByRole("button", { name: /Beanbag: floor, column 1, row 1/ })).toBeVisible();
  await expect(page.getByText("You have unsaved changes.")).toBeVisible();

  // Keyboard: select in the list, then arrows move and R turns.
  await placed(page).getByRole("button", { name: /Beanbag/ }).focus();
  await page.keyboard.press("ArrowRight");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("r");
  await expect(placed(page).getByRole("button", { name: /Beanbag: floor, column 2, row 2, turned 90°/ })).toBeVisible();
  // A move out of the room is refused at once and changes nothing.
  for (let i = 0; i < 7; i++) await page.getByRole("button", { name: "Move right" }).click();
  await expect(page.getByText("It does not fit there: the room ends.")).toBeVisible();
  await expect(placed(page).getByRole("button", { name: /Beanbag: floor, column 7, row 2/ })).toBeVisible();

  await page.getByRole("button", { name: "Save room" }).click();
  await expect(page.getByText("Your room is saved.")).toBeVisible();
  await expect(viewport(page)).toHaveAttribute("data-items", "furn_beanbag");

  await page.reload();
  await expect(placed(page).getByRole("button", { name: /Beanbag: floor, column 7, row 2, turned 90°/ })).toBeVisible();
  await expect(viewport(page)).toHaveAttribute("data-state", "3d", { timeout: 20_000 });
  await expect(viewport(page)).toHaveAttribute("data-items", "furn_beanbag");

  const me = await (await page.request.get("/api/v1/me")).json();
  await page.goto(`/profile/${me.handle}`);
  await expect(page.getByRole("img", { name: "Your room: Beanbag" })).toBeVisible({ timeout: 20_000 });
  await expect(viewport(page)).toHaveAttribute("data-items", "furn_beanbag");
});

test("a room shell that does not load shows the 2D plan, and the editor still saves", async ({ page }) => {
  await page.route("**/assets/3d/room/shell.*", (route) => route.fulfill({ status: 404, body: "" }));
  await newPlayer(page);
  await grantCoins(page, 1_000);
  await buy(page, "furn_floor_lamp");
  await page.goto("/room");

  await expect(viewport(page)).toHaveAttribute("data-state", "fallback", { timeout: 20_000 });
  await expect(page.getByText(SHELL_FAILED)).toBeVisible();
  await page.getByRole("button", { name: "Place Floor Lamp" }).click();
  await expect(page.getByRole("img", { name: "Your room: Floor Lamp" })).toBeVisible();
  await expect(viewport(page).getByText("FL", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Save room" }).click();
  await expect(page.getByText("Your room is saved.")).toBeVisible();
});

test("a room item that does not load shows a box and a notice; the saved room does not change", async ({ page }) => {
  await page.route("**/assets/3d/room/potted_plant.*", (route) => route.fulfill({ status: 404, body: "" }));
  await newPlayer(page);
  await grantCoins(page, 1_000);
  await buy(page, "furn_potted_plant");
  await page.goto("/room");
  await expect(viewport(page)).toHaveAttribute("data-state", "3d", { timeout: 20_000 });
  await page.getByRole("button", { name: "Place Potted Plant" }).click();
  await expect(page.getByText(ITEMS_FAILED)).toBeVisible();
  await page.getByRole("button", { name: "Save room" }).click();
  await expect(page.getByText("Your room is saved.")).toBeVisible();
  const room = await (await page.request.get("/api/v1/me/room")).json();
  expect(room.items.map((i: { sku: string }) => i.sku)).toEqual(["furn_potted_plant"]);
});
