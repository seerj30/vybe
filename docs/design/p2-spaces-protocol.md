# P2 design: real-time spaces protocol (stream V)

- Decisions: P2-1 public plaza, instances of max. 16 avatars; P2-2 SignalR in the existing API, binary protocol, one
  instance; P2-3 no free text (signed 2026-11-19, [P2 pack](../p2/README.md), [02](../p2/02-real-time-spaces.md),
  [01 option A](../p2/01-chat.md)).
- Spec: [§5 auth and errors](../spec/VYBE_Technical_Specification_v1.3.md#5-api-contracts-v1),
  [§11](../spec/VYBE_Technical_Specification_v1.3.md#11-security-and-privacy),
  [§12](../spec/VYBE_Technical_Specification_v1.3.md#12-observability-and-analytics),
  [§13](../spec/VYBE_Technical_Specification_v1.3.md#13-deployment-and-operations); decisions
  [6 (profile visibility)](../decisions/06-profile-visibility.md), [8 (privacy)](../decisions/08-legal-wording.md).
- Status: contract for stream V (server hub, load test) and stream W (web plaza scene). Values marked
  *provisional* are safe defaults for the beta; the load test can change them. A change after this PR is a new
  protocol version (§12).

## 1. Summary

| Item | Value |
|---|---|
| Hub URL | `/api/v1/spaces/hub` (same origin, behind Caddy with the rest of `/api`) |
| Transport | WebSockets only, no negotiate request (client: `skipNegotiation: true`, `transport: WebSockets`) |
| Hub protocol | SignalR **MessagePack** (`@microsoft/signalr` + `@microsoft/signalr-protocol-msgpack`). JSON only in test configurations (§11) |
| Auth | One-time **space ticket** from `POST /api/v1/spaces/tickets` (guest cookie or Supabase bearer, as every other endpoint), sent as `access_token` on connect |
| Space | `plaza`: instances of max. **16** avatars; the server picks or creates the instance |
| Authority | Server owns instance, slot, position, heading, movement state. Client sends intent only |
| Tick | **10 Hz** server tick; full snapshot of the instance per tick while anything moves, else 1 Hz |
| Input | move intent ≤ 10/s; emote, quick-chat, sticker: ids from fixed lists only. **No text field anywhere** |
| Storage | Nothing in the database. Presence in memory only. No positions or chat ids in logs |

## 2. Connect and authenticate

```
1. POST /api/v1/spaces/tickets            (normal auth; cookie callers send X-Requested-With: vybe-web)
   → 201 {"ticket":"<43 chars base64url>","expiresAt":"2026-11-19T10:00:30Z"}
2. WebSocket GET /api/v1/spaces/hub?access_token=<ticket>
3. SignalR handshake {"protocol":"messagepack","version":1}
4. invoke Join() → Welcome (§5)
```

- **Why a ticket.** Browsers cannot set `Authorization` on a WebSocket, and a Supabase JWT in a URL lives 1 hour. A
  ticket is random 256-bit, **single use**, valid **30 s**, kept in API memory only (one API instance, P2-2), and
  bound to one player. The cookie alone never opens the hub, so a cross-site page cannot open a socket as the
  player (cross-site WebSocket hijacking): it cannot get a ticket (CSRF header, CORS).
- **Ticket endpoint.** Same auth, CSRF and limits as other `POST` endpoints, plus **10 tickets per minute per player**
  (*provisional*). Codes: 401 `unauthenticated`, 404 `player_not_found` (Supabase identity without a player),
  403 `csrf_check_failed`, 403 `account_suspended`, 429 `rate_limited`. Guests are allowed (same as other social
  features). The endpoint goes into `packages/contracts` (OpenAPI) with the implementation PR.
- **Hub connect.** The server consumes the ticket in the upgrade request. A missing, unknown, used or expired ticket
  gets HTTP 401 before the upgrade. The server also checks `Origin`: it must be the public web origin (or a
  configured CORS origin); else 403. A `deleting` or `deleted` player, or a suspended player, gets 403.
- **Client set-up (stream W):**

```ts
new HubConnectionBuilder()
  .withUrl("/api/v1/spaces/hub", {
    transport: HttpTransportType.WebSockets,
    skipNegotiation: true,
    accessTokenFactory: () => api.createSpaceTicket().then((t) => t.ticket), // called again on every (re)connect
  })
  .withHubProtocol(new MessagePackHubProtocol())
  .withAutomaticReconnect(retryPolicy) // §8
  .build();
```

- **Transport timers.** Server keep-alive ping every 15 s; the server closes a connection silent for 30 s; the client
  uses the SignalR defaults (`keepAliveIntervalInMilliseconds` 15 000, `serverTimeoutInMilliseconds` 30 000). Handshake
  must finish in 5 s.
- **CSP.** The page connects to the same origin, so `connect-src 'self'` covers `wss:` on the same host in current
  browsers. No CSP change.

## 3. Space model

| Item | Value |
|---|---|
| Space id | `plaza` (only space in P2; friend room visits are a later space type) |
| Instance | integer id `instanceId` (≥ 1); shown as "Plaza 3". Created on demand, removed when empty for 60 s |
| Capacity | max. **16** avatars per instance; max. **64** instances (1,024 avatars) per API process (*provisional*) |
| Slot | integer 0–15, stable while the avatar is in the instance. All messages name avatars by slot |
| Bounds | square plaza, x and z in **[-1600, 1600] cm** (32 m × 32 m), y = 0; the scene can draw more, avatars stay inside |
| Spawn | random point within 400 cm of (0, 0), facing +z |
| Speed | max. **350 cm/s** (*provisional*); no running, no jumping (jump is an emote) |
| Collision | none between avatars; no static obstacles in P2 (the server clamps to the bounds only) |
| Units | position in whole centimeters; heading `h` in 0–255, angle θ = h × 2π / 256, facing vector (sin θ, cos θ) — `object.rotation.y = θ` for a model that faces +z |

**Instance choice on `Join`** (server only; the client cannot pick an instance in P2):

1. A reconnect within 30 s (§8) goes back to its reserved slot, if the instance still exists and no block (§9) now
   separates it.
2. Else the fullest instance with a free slot that contains no player in a block with the joiner (either direction).
3. Else a new instance. At the instance cap: `Join` fails with `spaces_full`, except when instances with free slots
   exist and only blocks that other players placed on the joiner keep them out: then a new instance opens past the cap
   (else many accounts that block one player could keep that player out of every plaza), up to one instance per allowed
   connection. The joiner's own blocks never open extra instances.

## 4. Messages: client → server (hub methods)

All argument values are integers or short ids. The server validates every value; a wrong type or count fails the
binding and counts as invalid input (§7). Names are case-insensitive in SignalR; use the names below.

| Method | Arguments | Kind | Result / effect |
|---|---|---|---|
| `Join()` | — | invoke (with result) | `Welcome` (§5). Fails with a `HubException` whose message is a code: `spaces_full`, `already_joined`, `rate_limited` |
| `Leave()` | — | send | Leaves the instance at once (no slot reservation). The connection stays open; `Join` again is allowed |
| `Move(dx, dz)` | two integers, each −127…127 | send | Move intent. Direction (dx, dz) in plaza axes; length 127 = full speed, ≥ 127 is clamped to 127, < 16 is stop |
| `Emote(sku)` | catalog SKU string, ≤ 40 chars | send | The SKU must be an emote item (`slot = emote`) that the player owns. Broadcast as `emote` |
| `QuickChat(id)` | phrase id, ≤ 32 chars | send | Id from `phrases` in `assets/manifests/quickchat.v1.json`. Broadcast as `quickChat` |
| `Sticker(id)` | sticker id, ≤ 32 chars | send | Id from `stickers` in the same manifest. Broadcast as `sticker` |
| `RefreshLook()` | — | send | Server reads the player's current avatar and outfit from the database and broadcasts `look` |

**Move rules (server).**

- The client sends `Move` when the intent changes, and **repeats it every 100 ms while the direction is not zero**.
  It sends `Move(0, 0)` once on stop. A hidden tab (Page Visibility API) sends `Move(0, 0)` and then no input.
- Each tick the server applies the latest intent: `position += normalize(dx, dz) × speed × min(|d|/127, 1) × 0.1 s`,
  then clamps to the bounds. A non-zero intent older than **500 ms** counts as stop, so a lost client stops walking.
- Heading follows the direction of movement; a stopped avatar keeps its last heading.
- The server never accepts a position, a speed, a heading or a time from the client.

**Expressions.** Emote, quick-chat and sticker have no target: everyone in the instance sees them. No text, number
or free string is broadcast that a player typed. The owned-emote set is read at `Join` and at `RefreshLook`, so a
purchase counts after the next `RefreshLook`.

## 5. Messages: server → client (client handlers)

Objects are MessagePack maps with the camelCase keys below (JSON in test configurations uses the same keys).

| Handler | Arguments | When |
|---|---|---|
| `snapshot` | `tick` (uint32), `avatars` (flat int array, 5 values per avatar) | Each tick while any avatar in the instance moved or changed state; else every 10th tick |
| `joined` | `Member` | Another avatar entered the instance. A `joined` for an occupied slot replaces that member (after a reconnect, handle or look can differ) |
| `left` | `slot` | An avatar left (leave, disconnect after the 30 s reservation, kick, or a block move) |
| `look` | `slot`, `Look` | An avatar's look changed (`RefreshLook`) |
| `emote` | `slot`, `sku` | |
| `quickChat` | `slot`, `id` | |
| `sticker` | `slot`, `id` | |
| `moved` | `Welcome` | The server moved this client to another instance (block, §9). Replace the scene state with the new `Welcome` |
| `error` | `code` | Non-fatal: the input was dropped (§7). At most one per second per code |
| `kicked` | `reason` | The server ends the connection right after this message (§8) |

**Snapshot avatar layout** (5 integers per avatar, all avatars of the instance, own avatar included):

| Index | Field | Range |
|---|---|---|
| 0 | `slot` | 0–15 |
| 1 | `x` cm | −1600…1600 |
| 2 | `z` cm | −1600…1600 |
| 3 | `heading` | 0–255 |
| 4 | `state` | 0 idle, 1 walking, 2 away (connection lost, slot reserved, §8) |

Size: about 10–14 bytes per avatar in MessagePack, ≤ 240 bytes per 16-avatar snapshot. `tick` increases by 1 every
100 ms of server time; the client interpolates with a 100–150 ms buffer and may predict its own avatar locally. It
corrects its own avatar toward the server position (snap when the error is above 100 cm).

**Types.**

```ts
interface Welcome {
  protocolVersion: 1;
  manifestVersion: number;   // quickchat.v1.json "version"; reload the page if it differs from the bundled manifest
  instanceId: number;
  slot: number;              // own slot
  tickHz: 10;
  bounds: { minX: -1600; maxX: 1600; minZ: -1600; maxZ: 1600 };
  speedCmPerS: 350;
  members: Member[];         // everyone in the instance, own member included
}
interface Member {
  slot: number;
  handle: string | null;     // null when the player's profile is private (decision 6): show a generic name
  look: Look;
}
interface Look {             // the GET /profiles avatar shape, with item SKUs instead of item ids
  bodyPresetId: string; skinToneId: string; facePresetId: string; hairPresetId: string; hairColorId: string;
  equipped: { hair: string | null; top: string | null; bottom: string | null; shoes: string | null;
              emote: string | null; accessories: (string | null)[] };
}
```

- `displayName` is never sent (free text).
- A `handle` lets the client open `/profile/:handle` (report, block, friend request) with the existing HTTP flows.
- Positions come only in `snapshot`; a client shows a `joined` member when its first snapshot entry arrives.
  Ignore snapshot entries for a slot without a member: `joined` and `snapshot` can arrive in either order.
- A client that reads slowly gets only the newest snapshot (the server skips older ones that are still queued).

## 6. Rates and sizes

| Item | Value (*provisional*) |
|---|---|
| Server tick | 10 Hz (100 ms), one loop for all instances |
| Snapshot send | 10/s while anything moves, else 1/s |
| Client `Move` | ≤ 10/s while moving (one per 100 ms) |
| Max. received message | **1 KB** (`MaximumReceiveMessageSize`); a larger frame closes the connection |
| Parallel invocations | 1 per connection; no client-to-server streaming |
| Download per player | about 9 MB/hour in a full, busy instance (pack estimate) |

## 7. Limits and validation (per connection)

| Limit | Value (*provisional*) | On excess |
|---|---|---|
| All client messages | token bucket 15/s, burst 15 | drop, `error("rate_limited")` |
| `Emote`, `QuickChat`, `Sticker` | 1 per 2 s each | drop, `error("rate_limited")` |
| `RefreshLook` | 1 per 5 s | drop, `error("rate_limited")` |
| `Join` | 5 per minute | `HubException("rate_limited")` |
| Dropped for rate | > 150 in 60 s | `kicked("rate_limited")` |
| Invalid input (out of range, unknown id, emote not owned, wrong argument type) | drop, `error("invalid_input")`; > 20 in 60 s | `kicked("invalid_input")` |
| Input before `Join` (except `Join`) | drop, `error("not_joined")` | counts as invalid input |
| No `Join` within 30 s of connect (or of `Leave`) | | `kicked("not_joined")` |
| No input (`Move` or expression) for 10 min | | `kicked("idle")` |
| Connections per player | 1: a new connection replaces the old one | old one gets `kicked("replaced")` |
| Connections per client address (IPv6 per /64) | 20 | HTTP 429 before the upgrade |
| Connections per API process | 1,100 | HTTP 503 `service_unavailable` before the upgrade |
| Raw inbound bytes (all frames, also frames that fail binding) | 2 KB/s, burst 4 KB | `kicked("rate_limited")` |
| Outbox (messages queued to a client that does not read) | 128 | `kicked("slow_client")` |
| Tickets | 10 per minute per player, single use, 30 s | 429 `rate_limited` |

`error` codes: `rate_limited`, `invalid_input`, `not_joined`. Unknown codes: ignore. `Join` can also fail with
`account_unavailable` (the connection is then closed with that kick reason).

## 8. Disconnect, kick and reconnect

**Slot reservation.** When a connection drops without `Leave` or a kick, the avatar stays in its slot for **30 s**
with `state = 2` (away), then `left`. A `Join` of the same player within 30 s gets the same instance, slot and
position (§3). After a deploy the API restarts with no state: every client joins a new instance.

**Kick reasons and client action.**

| `reason` | Meaning | Client |
|---|---|---|
| `server_shutdown` | API stops (deploy) | reconnect after a random 2–6 s, then the normal backoff |
| `replaced` | the same player connected again (other tab or device) | do not reconnect; show "Open in another tab" |
| `idle` | 10 min without input | do not reconnect; show a "Rejoin" button |
| `not_joined` | no `Join` in 30 s | reconnect on user action |
| `rate_limited`, `invalid_input` | abuse limit | do not reconnect for 60 s; then on user action |
| `suspended` | the player is suspended | do not reconnect; show the suspension message |
| `account_unavailable` | the player is `deleting` or deleted, or the session ended | do not reconnect; go to the start page |
| `spaces_full` | a block move found no instance with room | reconnect after 60 s |
| `slow_client` | the client did not read its messages | reconnect with the normal backoff |

**Automatic reconnect** (connection lost without `kicked`): delays 0, 2, 5, 10, 20, 30, 30 … s with ±25 % jitter;
stop after 5 minutes and show "Reconnect". Each attempt gets a new ticket (`accessTokenFactory`). After reconnect the
client calls `Join` again; the old connection's state is gone. A 401 or 403 on the ticket request stops the retries.

**Graceful shutdown (server).** On stop (SIGTERM; compose `stop_grace_period` 35 s) the hub stops accepting
connections, sends `kicked("server_shutdown")` to all, waits up to 2 s, and closes. Caddy waits up to 20 s for the
new API container (`lb_try_duration`), so the first reconnect ticket request usually succeeds.

**Account checks while connected.** Every 30 s the server reads the status of all connected players in one query:
suspended → `kicked("suspended")`; `deleting`/`deleted` → `kicked("account_unavailable")`.

## 9. Blocks and privacy

- **Blocked players never share an instance**, in either direction (spec invariant 8 intent, P2 pack). At `Join`,
  the server loads the player's blocks in both directions and skips instances with a blocked pair (§3).
- **Live block.** When A blocks B (`POST /me/blocks`) while both are in the same instance, the server moves **A (the
  blocker)** to another instance (`moved`). B sees only a normal `left` for A, so B does not learn about the block
  (spec §2 block privacy). This happens within 1 s of the block commit. A block also cancels a 30 s slot
  reservation that would put the pair together.
- Unblock changes nothing in running instances; the next `Join` follows the new state.
- A privacy change shows after `RefreshLook` (a `joined` that replaces the member) or at the next `Join`.
- **Private profile** (decision 6): `handle` is `null`; the avatar is still visible with its look.
- Spaces give **no** coins, XP, items, quest progress or achievements.

## 10. Logs, metrics and retention (decision 8)

| Kept | Where | Retention |
|---|---|---|
| Presence (who is in which instance, position, last intent) | API memory only | until leave or restart |
| Logs: connect, join, leave, kick with `playerId` (pseudonymous), `instanceId`, reason, duration, count of dropped and invalid messages | API JSON logs (existing pipeline) | operational logs, 30 days (spec §11) |
| Metrics: connections, instances, avatars, messages in/out per second by type, dropped and invalid by reason, kicks by reason, tick duration | OpenTelemetry meter `Vybe.Spaces` (no player ids) | metrics backend default |

**Never stored or logged:** positions, headings, movement, quick-chat or sticker ids per player, emote use per player,
who was near whom, tickets, the query string of the hub URL. No message content exists to store: there is no free
text. The privacy notice needs a "presence in spaces" row (P2 pack; orchestrator, to confirm with counsel).

## 11. Hosting notes (implementation PR)

- Caddy (`infra/oracle/Caddyfile`, `infra/web/Caddyfile`): an own route for the hub path, without the body cap.
  Measured with Caddy 2.11.9: it offers WebSockets on HTTP/1.1 only (no RFC 8441 on HTTP/2), and an upgraded
  connection stays open past `read_body 30s`, `write 60s` and 150 KB of client frames. `infra/oracle/test-stack.sh`
  checks a hub socket through Caddy for 65 s (origin check, single-use ticket, join). The access log stays off (the
  ticket is in the query string); API traces drop `url.query` and skip the hub request.
- API: SignalR (shared framework) plus the MessagePack protocol package. Production accepts MessagePack only;
  `Spaces:AllowJsonProtocol` = true only in Development and tests.
- One API process holds all state (P2-2). A second process needs a backplane and sticky routing: out of scope.
- `packages/contracts`: the ticket endpoint in OpenAPI (`createSpaceTicket`, schema `SpaceTicket`); the hub types,
  names and constants of §4–§8 in `packages/contracts/src/spaces.ts`, imported as `@vybe/contracts/spaces`.

## 12. Versioning

- `Welcome.protocolVersion` = 1. A breaking change (new field meaning, new required argument, changed limits that a
  client must know) increases it; the client reloads the page on a mismatch.
- `quickchat.v1.json`: ids never change or go away inside one version. New phrases or stickers: new version file;
  the server accepts the ids of the version it ships.
- New optional handlers or fields can come without a version change; clients ignore unknown handlers and keys.

## 13. Open questions

| # | Question | Default until answered |
|---|---|---|
| 1 | Report or block from the plaza for a player with a private profile (no handle)? | Not possible in P2; a later `Report(slot, reason)` hub method can map the slot to the player server-side |
| 2 | Join a friend's instance ("go to friend")? Shows presence to friends | Not in P2 (decision 8: presence is new personal data) |
| 3 | Sticker art (12 icons) | Stream W draws them (flat 2D, `assets/` or `apps/web`), ids from the manifest |
| 4 | Final limits (speed, bounds, rates, caps) | The *provisional* values here; the load test (`docs/runbooks/load-test.md`) may change caps |
| 5 | 20 connections per address: enough behind carrier-grade NAT (many players, one address)? | Keep 20; raise `Spaces:MaxConnectionsPerAddress` if real players hit it (the 429 is in the API metrics) |
