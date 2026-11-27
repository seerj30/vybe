# P2 design: live friend room visits (stream X)

- Decisions: P2-1 "friend room visits if time allows" ([P2 pack](../p2/README.md), option S1 in
  [02](../p2/02-real-time-spaces.md)); P2-3 no free text; [decision 6 (profile visibility)](../decisions/06-profile-visibility.md);
  [decision 8 (privacy)](../decisions/08-legal-wording.md). Product rules from the product owner, 2026-11-27 (below).
- Builds on: [spaces protocol](p2-spaces-protocol.md) (hub, tickets, limits, blocks), [My Room](p1-my-room.md)
  (layout, scene), [friends](p1-friends-leaderboard.md) (mutual friendship).
- Status: contract for stream X. Values marked *provisional* can change after real use.

## 1. Product rules (product owner, 2026-11-27)

1. The owner opens the room with "Open my room to friends" in My Room (desktop and phone). The open room is a live
   space: the owner plus up to **7 visitors** (8 avatars). The scene is the owner's **saved** room layout.
2. Only the owner's **accepted friends** can enter. A pair with a block in either direction never shares the room
   (the plaza rule). A private-profile owner is no exception: friendship is mutual, as in the friend list.
3. Friends learn that the room is open **only while it is open**: an "Open room · Visit" entry in the friend list.
   Nothing else shows that the owner is online (no passive presence, decision 8).
4. The owner closes the room with a button, or by leaving, or by a lost connection after the normal 30 s grace.
   Visitors then see "The room closed" with a way to the plaza or the hub.
5. In the room: walk, emotes, quick-chat and stickers exactly as in the plaza (same manifest, same limits, no free
   text). The owner can remove a visitor. Every member can report and block another member.
6. Visitors cannot edit the room. The owner cannot edit while the room is open.

## 2. Choice: a space kind on the same hub

A room is a second **space kind** in the existing `SpaceManager` and `SpaceHub`, not a second hub.

| | Space kind (chosen) | Second hub |
|---|---|---|
| Connection, ticket, Origin check, caps per address and per process | Shared, unchanged | A copy, or shared through new code |
| One connection per player (`replaced`) | Free: a player is in the plaza **or** in a room | Two hubs need a cross-hub rule |
| Live block, account check, shutdown, outbox, metrics | One code path | Two code paths |
| Plaza risk | New branches in the manager | None in the manager |

The plaza code paths stay the same: room instances live in their own dictionary, so plaza placement never sees
them, and every room rule runs only for a room instance.

## 3. Protocol additions (protocol version stays 1)

New hub methods. All are new; old clients never call them. New `Welcome` fields are optional (§12 of the
protocol doc), so `protocolVersion` stays 1.

| Method | Arguments | Kind | Result / effect |
|---|---|---|---|
| `OpenRoom()` | — | invoke | Opens the caller's room and places the caller in it as the owner. Returns `Welcome` (`space = "room"`). A reconnect within 30 s gets the same room back. `HubException`: `spaces_full` (room cap), `already_joined`, `rate_limited`, `account_unavailable` |
| `JoinRoom(handle)` | owner handle, ≤ 20 chars | invoke | Enters the open room of a friend. Returns `Welcome`. `HubException`: `room_unavailable` (not open, not a friend, removed, or a block with a member — one code, so the answer tells nothing more), `room_full`, `already_joined`, `rate_limited`, `invalid_input`, `account_unavailable` |
| `CloseRoom()` | — | send | Owner only: closes the room at once. Visitors get `kicked("room_closed")`. The owner's connection stays open (not joined) |
| `RemoveVisitor(slot, key)` | member slot and key | invoke | Owner only: the visitor gets `kicked("removed")` and cannot enter again while this opening lasts. `HubException`: `not_found`, `rate_limited`, `invalid_input` |

`Leave()` of the owner closes the room. `Leave()` of a visitor leaves it. `Join()` (plaza) while in a room fails
with `already_joined`, as before.

**`Welcome` (new optional fields).**

```ts
interface Welcome {
  // … all plaza fields; in a room: instanceId = a room number (not shown), bounds and speed of the room
  space?: "plaza" | "room";   // missing = "plaza"
  ownerSlot?: number | null;  // room: the owner's slot
  room?: SpaceRoom | null;    // room: the owner's saved layout at open time
}
interface SpaceRoom { wall: string | null; floor: string | null; items: SpaceRoomItem[] } // finish SKUs
interface SpaceRoomItem { sku: string; surface: "floor" | "wall_back" | "wall_left"; x: number; y: number; rotation: 0 | 90 | 180 | 270 }
```

The layout has SKUs only (no item ids, no revision, no time), the same facts that `GET /profiles/{handle}/room`
shows. A private owner's friends get it here, because the profile room endpoint gives them 404.

**New kick reasons** (the client does not reconnect automatically):

| `reason` | When | Client |
|---|---|---|
| `room_closed` | The owner closed the room, left, or did not come back within 30 s | "The room closed." Buttons: plaza, hub |
| `removed` | The owner removed the visitor, the owner blocked the visitor, or the friendship ended | "You are no longer in this room." Buttons: plaza, hub |
| `left_room` | The visitor blocked a member: the blocker leaves (plaza rule) | "You blocked a player and left the room." Buttons: plaza, hub |

A visitor cannot tell a remove from a block by the owner: both are `removed` (spec §2 block privacy).

**HTTP changes.**

- `GET /me/friends`: each `FriendEntry` gets `roomOpen` (boolean): true only while that friend's room is open, and
  never for a visitor that the owner removed in this opening or a viewer in a block with a member (§5). Computed
  from API memory at request time.
- `PUT /me/room`: 409 `room_open` while the caller's room is open (rule 6).

## 4. Room state (API memory only)

| Item | Value |
|---|---|
| Room instance | One per open room; key = owner player id; never in the plaza instance list |
| Capacity | 8 avatars: the owner plus 7 visitors (*provisional*) |
| Cap | `Spaces:MaxRooms` = 128 open rooms per API process (*provisional*; 1,024 avatars). At the cap `OpenRoom` fails with `spaces_full` |
| Bounds | From `room.v1.json`: floor 8 × 0.5 m = 4 m, centered on (0, 0); the back wall at z = −200 cm, the left wall at x = −200 cm. Avatars stay 30 cm inside: x and z in **[−170, 170] cm** |
| Spawn | Owner: the center. Visitor: a random point within 80 cm of the front edge center (0, 120) |
| Speed | **250 cm/s** (*provisional*; the plaza speed is too fast for a 4 m room) |
| Collision | None between avatars. **Furniture blocks** (product owner, 2026-11-27): see "Furniture collision" below |
| Removed set | Player ids that the owner removed in this opening; deleted when the room closes |
| Layout | Read once at `OpenRoom` (the saved layout). It cannot change while open (409 `room_open`) |

**Furniture collision.** The server tick is the authority (`RoomCollision.cs`). The web predictor runs the same
rules (`room-collision.ts`), so the own avatar does not rubber-band at furniture.

1. **Obstacles.** At `OpenRoom` the server makes one rectangle per floor item on the `object` layer of `room.v1.json`:
   the footprint after the rotation (90° and 270° swap `w` and `d`), in cm (cell `x`, `y` → x = −200 + 50 `x`,
   z = −200 + 50 `y`), grown by the **avatar radius of 20 cm** on each side (*provisional*; about the shoulder width
   of the avatar). Rugs, wall items and SKUs that the manifest does not know never block. The layout cannot change
   while the room is open, so the obstacles stay the same until the room closes.
2. **Movement.** The avatar is a point. A tick first moves along x, then along z. Each part stops at the edge of an
   obstacle that it enters from outside. So a diagonal walk into an obstacle **slides** along its edge. Only the inside
   of a rectangle blocks: an avatar can stand on an edge and walk along it.
3. **No stuck avatar.** An obstacle never blocks an avatar that is already inside it: that avatar walks out freely
   and then cannot walk back in. A spawn point (owner: the center; visitor: near the front) inside an obstacle moves
   to the nearest free point of a 10 cm grid. Only a floor fully covered with furniture has no free point; then the
   avatar stays and walks out (rule above). Reconnects keep the old position, which was free.
4. **Plaza.** Plaza instances have no obstacles: the tick is the same clamp as before.
5. **Tests.** Shared vectors (`packages/contracts/fixtures/room-collision.v1.json`) run in the API (`RoomCollisionTests`)
   and in the web predictor (`room-collision.test.ts`): blocked moves, sliding, rotations, rugs, wall items, an avatar
   inside an obstacle, gaps.

A layout can cut the floor into parts (for example a row of furniture from wall to wall). Then avatars in different
parts cannot meet, and a visitor can spawn in another part than the owner. This is the owner's layout choice; the
server does not refuse it (open question 2).

**Owner presence.** The owner's avatar must stay in the room. When the owner's connection drops, the avatar is
"away" for 30 s (as in the plaza) and the room stays open. After 30 s, or on `Leave`, `CloseRoom`, a kick, a
suspension, a deletion, or a `Join` somewhere else, the room closes. A reserved slot in another space (plaza or
room) is dropped when the player opens or enters a space.

## 5. Friend and block rules

- `JoinRoom` reads, before the state lock: the owner by handle, an accepted friendship, the joiner's profile and
  blocks (the plaza query). A non-friend gets `room_unavailable` before the server looks at the room, so the answer
  never tells whether a non-friend's room is open.
- The joiner's blocks (both directions) with **any** member keep the joiner out (`room_unavailable`).
- **Friendship ends** (`DELETE /me/friends/{handle}` by either side, or a block): the visitor is removed at once
  (`removed`). A friendship that ends while a `JoinRoom` is between its database read and the lock also keeps the
  joiner out (the manager keeps such pair ends for 5 minutes, as live blocks).
- **Block** (hub `Block` or `POST /me/blocks`):
  - the owner blocks a visitor → the visitor gets `removed`;
  - a visitor blocks the owner or another visitor → the blocker gets `left_room` (the blocker moves, as in the
    plaza); the blocked player sees a normal `left`.
- **Friend list.** `roomOpen` is false while a member inside has a block with the viewer (either direction; database
  blocks plus the live blocks of the manager), the same rule as `JoinRoom`. Else a viewer who sees "open" but always
  gets `room_unavailable` could tell that a member blocked them (spec §2 block privacy).
- `Report` works as in the plaza (fixed reasons, no text).

## 6. Limits

Every plaza limit (§7 of the protocol doc) applies per connection. In addition:

| Limit | Value (*provisional*) |
|---|---|
| `OpenRoom`, `JoinRoom` | Count as `Join`: 5 per minute together |
| `RemoveVisitor` | 1 per s (the `Report`/`Block` interval) |
| Room handle argument | `PlayerHandles` input rules (3–20 letters, digits, `_`); else `invalid_input` |
| Friend check reads | In the same 8-way database gate as the profile reads |

## 7. Privacy (decision 8)

- An open room and its members are in API memory only. Nothing about a visit goes into the database.
- Logs: a room join logs the player id and the space kind (`room`), never the owner id or a room number, so no
  log line connects a visitor with an owner. Connection close lines stay as in the plaza (player id, reason,
  duration). Two close lines with the same time can still suggest that players were together; this is the plaza
  rule too (30-day operational logs). Counsel question 9 covers it.
- Metrics (`Vybe.Spaces`): a new gauge `vybe.spaces.rooms` (open rooms); room methods in `messages_in`; room kick
  reasons in `kicks`. No player ids.
- Privacy notice draft: a new row "Open room presence": friends see that your room is open while it is open, and
  who is in it while they are in it.

## 8. Web

| Route | Who | What |
|---|---|---|
| `/room` | owner | My Room editor. New card "Open my room to friends" (disabled with unsaved changes) → `/room/live` |
| `/room/live` | owner | The live room (fullscreen). "Close room" button; Remove on each visitor in the member list |
| `/visit/:handle` | friend | The live room of that friend (fullscreen) |
| `/friends` | friend | "Open room · Visit" on a friend whose room is open |

- **One page component.** The plaza page gets a `space` input from the route (`plaza`, `room-host`,
  `room-visit`). The plaza value keeps the plaza page as it is. The connection client calls `Join`, `OpenRoom`
  or `JoinRoom(handle)`.
- **Scene.** The plaza renderer gets a room view: the room shell, finishes and furniture (shared code with the
  My Room renderer) instead of the plaza ground, and a fixed camera in front of the open side. Screen up walks
  toward the back wall; the input turns 180° for that camera.
- **Member list.** As in the plaza (Report, Block, Profile), plus "Host" on the owner and, for the owner,
  "Remove" on each visitor.
- **End states.** `room_closed`, `removed`, `left_room`, `room_unavailable`, `room_full`: an overlay with "Go to
  the plaza" and "Back to the hub" (the owner: "Back to My Room").
- **Phone (375 px).** The plaza layout (joystick, actions column, bottom sheet). The header shows the owner name
  and the Close button.

## 9. Test plan

- **API (xUnit, real PostgreSQL, `RoomVisitTests`)**: open and join (friend only, private owner, non-friend gets
  `room_unavailable`), capacity 8 and `room_full`, owner close and owner `Leave` kick visitors with `room_closed`,
  owner disconnect keeps the room 30 s then closes, owner reconnect gets the room back, remove plus no re-entry,
  block rules (owner blocks → `removed`; visitor blocks → `left_room`), unfriend removes, block with a member keeps
  a joiner out, `roomOpen` in `GET /me/friends` (true only for friends while open), `PUT /me/room` 409 while open,
  bounds (−170…170), expressions in a room, plaza tests unchanged. Furniture (`RoomCollisionTests` and
  `RoomVisitTests`): the shared vectors, the free spawn of owner and visitor, a stop at the bed edge through the hub,
  the plaza rule unchanged without obstacles.
- **Web unit (Vitest)**: Welcome parsing of the new fields, room kick reasons and join errors, the client join
  method per space, the input turn for the room camera, the predictor on the shared furniture vectors.
- **E2E (Playwright, real hub)**: three guests who are friends: A opens the room, B and C see "Visit" and enter,
  all see 3 members; A removes C (C sees the remove state); A closes (B sees "The room closed"). Phone 375 px check
  of the live room.

## 10. Open questions

| # | Question | Default until answered |
|---|---|---|
| 1 | Also show open rooms on the hub (a card)? | Friend list only |
| 2 | Furniture collision: a layout can cut the floor into parts that avatars cannot leave. Refuse such layouts in the editor, or spawn visitors in the owner's part? | Allowed; the spawn is the nearest free point (§4) |
| 6 | Avatar radius 20 cm: a gap of one cell (50 cm) between two items leaves 10 cm to pass. Is that the wanted feel? | 20 cm (*provisional*) |
| 3 | Edit while open? | Not possible: the editor sends the owner to the live room; the API refuses saves (409) |
| 4 | Let the owner invite a friend (a notification)? | No: a notification is a new channel (decision 8 review) |
| 5 | Room capacity 8 and room cap 128 | Provisional; the load test can change them |
