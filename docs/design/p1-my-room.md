# P1 design: My Room 3D and additional cosmetics (stream Q)

- Spec: [§16 P1](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates) ("My Room 3D, additional cosmetics"), [§8 economy](../spec/VYBE_Technical_Specification_v1.3.md#8-xp-quests-catalog-and-economy), [§9 assets](../spec/VYBE_Technical_Specification_v1.3.md#9-avatar-3d-asset-contract)
- Decisions: [3 art](../decisions/03-art-direction.md) (code-built art, no AI assets), [5 economy](../decisions/05-economy.md) (price bands), [6 profile visibility](../decisions/06-profile-visibility.md)
- Art pipeline: [docs/art/README.md](../art/README.md)
- Status: decided by stream Q on 2026-11-11. Open product questions are at the end.

## Summary

Each player has one room: a small 3D diorama with a floor, two walls and furniture. The player buys room
items in the shop (they are normal catalog items in new slots), places them on a grid in `/room`, and saves
the layout. Other signed-in players see the room on the profile, with the same visibility rules as the
profile. The server stores the layout and validates every save. It never trusts a layout from the client.

The release also adds 13 avatar cosmetics (catalog version 3, 3 of them epic), so the shop grows from 40 to 53
priced avatar items, plus 31 priced room items (84 in all).

## 1. The room

| Topic | Rule |
|---|---|
| Floor | 8 × 8 cells. One cell is 0.5 m, so the floor is 4 m × 4 m |
| Walls | Two walls: `wall_back` (along the back edge of the floor) and `wall_left` (along the left edge). Height 2.5 m. The front and the right side are open, so the camera can see in |
| Wall grid | Each wall has 8 columns × 5 rows of 0.5 m. Row 0 is at the floor |
| Coordinates | Floor: `x` 0–7 from left to right, `y` 0–7 from back to front. Back wall: `x` = column 0–7 from left to right, `y` = row 0–4 from the floor up. Left wall: `x` = column 0–7 from back to front, `y` = row 0–4 |
| Shell | Built in. A player with no finish items sees the default floor and walls |
| Finishes | One wall finish (slot `room_wall`) and one floor finish (slot `room_floor`) at a time, or none (the default shell) |
| Max items | 40 placed items. A player can own fewer, because each item is a non-stackable cosmetic |
| One copy | Inventory has one row per item (spec §4), so each owned item can be placed once |

### Item categories

| Slot | Placed on | Examples |
|---|---|---|
| `furniture` | Floor | Sofa, bed, desk, chairs, tables, plants, lamps, an arcade machine, rugs |
| `wall_decor` | `wall_back` or `wall_left` | Posters, a wall shelf, a clock, a mini backboard, a neon sign |
| `room_wall` | The two walls (finish) | Wall colors and patterns |
| `room_floor` | The floor (finish) | Wood, tiles, a court floor |

The avatar slots stay as they are: `hair`, `top`, `bottom`, `shoes`, `accessory`, `emote`. The equip
endpoint refuses a room slot (422 `validation_failed`), and outfits never contain room items.

### Placement rules

1. **Footprint.** Every placeable item has a footprint in cells: `w × d` on the floor (1–4 each), `w × h` on a
   wall (1–4 columns, 1–3 rows). The footprint comes from the room manifest, never from the client.
2. **Snap to grid.** A position is the integer cell of the footprint corner with the smallest `x` and `y`.
3. **Rotation.** Floor items turn in 90° steps: `rotation` is 0, 90, 180 or 270. At 90 and 270 the footprint
   is `d × w`. Wall items always face into the room: `rotation` must be 0.
4. **Bounds.** The rotated footprint must be fully inside the grid: `0 ≤ x`, `x + w ≤ 8`, `0 ≤ y`, and
   `y + d ≤ 8` (floor) or `y + h ≤ 5` (wall).
5. **Layers.** A floor item is on the `rug` layer (flat floor covers) or the `object` layer (everything else).
   Two items on the same layer must not share a cell. A rug can be under objects. Wall items on the same
   wall must not share a cell.
6. **Floor and wall items do not collide.** A wall item can hang above a floor item. Wall items are flat
   (depth ≤ 0.3 m), and the player decides how it looks.
7. **Surface.** `furniture` goes only on `floor`; `wall_decor` only on `wall_back` or `wall_left`.

The web editor checks the same rules before a save (shared rules in `room-layout.ts`), so the player sees a
problem at once. The server checks them again on every save.

## 2. Storage and API

### Table `player_rooms`

| Column | Type | Note |
|---|---|---|
| `player_id` | `uuid` PK, FK `players` | One room per player |
| `wall_item_id` | `uuid NULL`, FK `catalog_items` | Wall finish; NULL = default |
| `floor_item_id` | `uuid NULL`, FK `catalog_items` | Floor finish; NULL = default |
| `layout_json` | `jsonb NOT NULL DEFAULT '[]'` | `[{"itemId":uuid,"surface":"floor"\|"wall_back"\|"wall_left","x":int,"y":int,"rotation":0\|90\|180\|270}]` |
| `revision` | `bigint NOT NULL DEFAULT 0` | Incremented by the existing `increment_revision` trigger on every UPDATE (spec §4 invariant 5) |
| `created_at`, `updated_at` | `timestamptz` | |

CHECK constraints: `layout_json` is a JSON array with at most 40 elements; `revision >= 0`. The full
placement rules need the manifest footprints, so the API checks them (the database cannot).

A player without a row has the empty default room (revision 0). The first save creates the row.

`catalog_items.slot` gets the four new values (`furniture`, `wall_decor`, `room_wall`, `room_floor`). This is
an expand change: the CHECK constraint accepts more values; no existing row changes. Room items list both
body presets in `compatible_body_presets` (the column requires one or more), and the field has no effect on
them.

### Endpoints

| Operation | Request | Response |
|---|---|---|
| `GET /me/room` | — | `200 Room` |
| `PUT /me/room` | `RoomLayoutRequest` + `X-Requested-With: vybe-web` | `200 Room` |
| `GET /profiles/{handle}/room` | — | `200 Room` or `404 not_found` |
| `GET /catalog/items?category=room` | `category`: `avatar` or `room` (new, optional) | Catalog page |

`Room`: `{ revision, wallItem, floorItem, items: [{ itemId, sku, surface, x, y, rotation }], updatedAt }`.
`wallItem` and `floorItem` are `{ itemId, sku }` or null. The `sku` lets the web find the art in the room
manifest.

`RoomLayoutRequest`: `{ expectedRevision, wallItemId, floorItemId, items: [{ itemId, surface, x, y, rotation }] }`.

### Save rules (`PUT /me/room`)

The save replaces the whole room, all or nothing. In one transaction, under a row lock on the room:

| Check | Answer |
|---|---|
| Body is not valid JSON, a field has the wrong JSON type (for example `x: 1.5`), an unknown field | 400 `malformed_request` |
| A required field is missing, an unknown `surface`, a duplicate `itemId` | 422 `validation_failed` |
| More than 40 items | 422 `room_item_limit` |
| `rotation` is not 0/90/180/270, or a wall item has a rotation other than 0 | 422 `validation_failed` |
| The caller does not own an item (placed item, wall or floor finish) | 403 `item_not_owned` |
| Item slot does not match the surface or the finish field (for example a sofa on a wall, a hat in the room) | 422 `incompatible_item` |
| Item is inactive | 422 `item_inactive` |
| Footprint outside the grid | 422 `placement_out_of_bounds` |
| Two items on the same layer or wall share a cell | 422 `placement_overlap` |
| `x` or `y` below 0 or above 63 (checked before any arithmetic) | 422 `placement_out_of_bounds` |
| Suspended account | 403 `account_suspended` |
| Account in deletion (the save waits for a running deletion with `FOR SHARE` on the player row) | 401 `unauthenticated` |
| `expectedRevision` is not the stored revision | 409 `revision_conflict` |

A refusal for one item adds `itemId` to the problem (`placement_*`, `incompatible_item`, `item_inactive`,
`item_not_owned`), so the web can mark that item.

**Idempotent:** a save whose content equals the stored room returns `200` with the stored room and does not
change the revision, also when `expectedRevision` is old. So a retry after a lost answer succeeds. A
different layout with an old revision gets 409 `revision_conflict` (another tab saved first).

**Lost update:** the save first inserts an empty row (`ON CONFLICT DO NOTHING`), then reads it `FOR UPDATE`
and compares the revision. Two first saves with `expectedRevision: 0` cannot both win.

### Who can see a room

`GET /profiles/{handle}/room` follows decision 6 and the profile endpoint exactly:

| Caller | Answer |
|---|---|
| Anonymous | 404 `not_found` |
| Unknown, deleting or deleted handle | 404 `not_found` |
| Private profile, caller is not the owner | 404 `not_found` |
| The owner | The room |
| Any other signed-in player (guest or registered) | The room |
| A block in either direction between caller and owner | The **empty default room** (revision 0, no items, no finishes, `updatedAt` null) |

The block case does not give 404, because the profile itself stays visible (ADR 0005: a 404 for a blocked
caller only would tell that caller about the block). The empty room is the same answer as for a player who
never decorated, so the blocked player cannot see the room and does not learn about the block. For the same
reason, other players always get `revision` 0 and `updatedAt` null (only the owner's `GET /me/room` has them),
the endpoint runs the same queries for a blocked and a not-blocked caller, and an empty first save stores no
row.

Every answer carries `Cache-Control: private, no-store` and `X-Robots-Tag: noindex`, as the profile.

### Other effects

- **Data export (J4):** the export gets a `room` section (finishes and layout).
- **Deletion (J4):** the anonymization deletes the `player_rooms` row, as it deletes saved outfits.
- **Seeding:** `ContentSeeder` upserts the room items from `assets/manifests/room.v1.json` the same way as
  avatar items (by sku, idempotent).
- **Catalog version:** avatar catalog version 3; room manifest version 1. Version-1 and version-2 skus,
  prices and rules do not change.

## 3. Art

Same rules as stream N: Python scripts only, Blender 4.5 LTS in the pinned Docker image, reproducible bytes,
thumbnails, manifest entries, budget checks in CI, a row in `assets/LICENSES.md`, no AI assets.

| Asset | Content |
|---|---|
| Room shell | `room_shell.glb`: nodes `shell_floor`, `shell_walls`, `shell_trim` |
| Wall finishes (4 meshes, 5 skus) | A mesh of the two walls that replaces `shell_walls` |
| Floor finishes (3) | A mesh of the floor that replaces `shell_floor` |
| Furniture (18, 2 of them rugs) | Static meshes (no rig). Origin at the footprint center on the floor, front to +Z (glTF) |
| Wall decor (5) | Static meshes. Origin at the bottom center of the back face, front to +Z |
| Avatar cosmetics (13) | On the existing rig (`vybe-rig-1`), new builders and color variants |

The room manifest `assets/manifests/room.v1.json` holds the grid, the shell and one entry per room item:
`sku`, `slot`, `name`, `price`, `minLevel`, `rarity`, `assetKey`, `assetVersion`, `assetUrl`,
`thumbnailUrl`, `footprint {w, d}` or `{w, h}`, `layer` (floor items), `surfaces[]`, `textureVariants[]`,
`triangles`, `drawCalls`, `downloadBytes`, `contentHash`.

### Budgets (goals for low-end mobile)

| Budget | Limit | Check |
|---|---|---|
| Triangles per room item | 1,500 | `check_assets.py` |
| Draw calls (materials) per room item | 6 | `check_assets.py` |
| Download per room item | 120 KB | `check_assets.py` |
| Item mesh inside its footprint box | ±2 cm; height ≤ 2.4 m (floor); depth ≤ 0.3 m (wall) | `check_assets.py` |
| Shell download (first view of an empty room) | 150 KB | `check_assets.py` |
| Worst-case room scene: shell + heaviest finishes + the 40 heaviest placeable items | 50,000 triangles, 150 draw calls | `check_assets.py` |
| Worst-case room download (all files of that scene) | 3 MB | `check_assets.py` |
| Textures | None (flat colors, like the avatar) | `check_assets.py` |

Reached with this release (`python3 assets/tools/check_assets.py`): at most 368 triangles, 6 draw calls and
8 KB per room item; 1,524 triangles for the heaviest finish; a 3 KB shell; a worst-case room of 6,960 triangles,
95 draw calls and about 150 KB.

## 4. Web

- **`/room` (editor).** Lazy Three.js `RoomViewport` (the same lazy import, flat toon look and context-loss
  handling as `AvatarViewport`). A fixed orthographic three-quarter camera from the open front-right corner
  shows both walls; the scene renders only on a change (no animation loop).
- **Place:** pick an owned item in the tray, then "Place" puts it at the first free cell (walls: from eye
  height). Focus then goes to the item's controls, and the room scrolls into view on a phone. **Move:** tap or click
  a floor cell (or wall cell) to move the selected item there, or use the arrow buttons or arrow keys.
  **Rotate:** button or `R`. **Remove:** button or `Delete`. **Wall:** a switch moves a wall item between
  the back and the left wall. **Finishes:** two selects (walls, floor). **Save:** sends the full layout.
  "Unsaved changes" shows until the save succeeds. A blocked move shows the reason at once and does not
  change the layout.
- **Accessible:** every action has a button with a label (44 px targets) and a keyboard shortcut; a text list
  of placed items with their positions; a polite live region announces each change; the canvas is
  decorative (`aria-hidden`) and the list is the source of truth for screen readers.
- **Fallback:** no WebGL, a failed shell file, or a lost context → a 2D top-down grid (CSS grid with item
  initials) and a non-blocking notice. The editor works fully in 2D. A failed item file shows a neutral box
  of its footprint and the notice "Some room items could not load".
- **Mobile first:** the viewport is full width; the tray and the controls are below it. Reduced motion: no
  camera animation.
- **Profile:** a "Room" section on a visible profile with a view-only `RoomViewport`.
- **Shop:** the filter gets a "Room" group (furniture, wall decor, walls, floors) and a `category=room` option
  ("All room items"; `/shop?filter=room` opens it). An owned room item links to `/room` ("Place it"). The page
  size becomes 100 (the catalog has 84 priced items).
- **Navigation:** a "Room" link in the main navigation.
- **Wardrobe:** lists avatar items only; room items link to `/room`.

## 5. Tests

| Level | Cases |
|---|---|
| Asset checker (unit) | Room manifest fields, footprint bounds, item budgets, worst-case room budgets, price bands, rig-free files |
| Layout rules (TS unit, C# unit) | Rotation swap, bounds, overlap per layer, rug under object, wall rows, max items |
| API (real PostgreSQL) | Save and read; idempotent replay; revision conflict; unowned item 403; out of bounds; overlap; wrong surface; too many items; inactive item; wall item with rotation; equip a room item refused; profile room for a signed-in player; private profile 404; anonymous 404; blocked in either direction → empty room; owner sees own private room; export contains the room; deletion removes it |
| Playwright E2E (`room.spec.ts`, `touch.spec.ts`) | Buy a furniture item in the shop, place it, move and turn it with the keyboard, a refused move, save, reload shows it, the own profile shows it; the 3D shell fails to load → 2D plan, notice, the editor still saves; an item file fails → box and notice, the saved room is correct; a tap in the 3D room moves the selected item (phone) |

## 6. Open product questions

1. **Blocks:** a blocked player sees an empty room (not a 404) so the block stays private. Is that the wanted
   behavior, or should a block hide the whole profile?
2. **Avatar in the room:** the room shows furniture only. Should the owner's 3D avatar stand in the room
   (about 3,500 more triangles, one more viewport load)?
3. **One copy per item:** a player can place each item once (inventory is non-stackable). Should some room
   items (for example plants) be stackable, with a count in the inventory?
4. **Room size:** one 8 × 8 room for everybody. Should a bigger room be a reward (level or an epic item)?
5. **Moderation:** layouts contain only catalog items and grid positions (no text). Is a "report room" action
   needed, or is the profile report enough?
6. **Friends visibility:** stream O adds friends. Should a room have its own "friends only" setting later?
