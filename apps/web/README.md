# VYBE web app (`apps/web`)

Angular standalone app (spec §3, §10) on the VYBE API (`services/api`, ADR 0003). The client never
writes wallet, inventory or scores: it shows what the API returns and loads `/me` again after a
change.

| Route | Screen |
|---|---|
| `/` | Landing: start as guest, continue, "unavailable" state |
| `/play` | Guest onboarding: create a guest, resume, lost-guest notice |
| `/avatar/create` | Avatar creator with a 2D fallback portrait (until the 3D art exists) |
| `/hub` | Avatar, level, XP, wallet |
| `/arcade` | Game list |
| `/arcade/street-hoops` | Street Hoops on an API session: server-verified score and rewards |
| `/shop` | Catalog with slot filter; purchase with confirmation |
| `/wardrobe` | Owned items per slot; wear and take off (two accessory points) |
| `/wallet` | Balance and V-Coin ledger |
| `/practice` | Client-only Street Hoops prototype (no API, no rewards, dev tuning panel) |
| `/settings` | Account state and linking, blocked players, data export, account deletion |
| `/deletion/:requestId` | Status of a deletion request (no session needed: the id is the access key) |
| `/admin` | Staff sign-in; reports, suspensions (moderator); catalog, wallet adjustment, audit log (administrator) |

## API client

`src/app/api/api-client.ts` is a small typed client over the `@vybe/contracts` types:

- Same-origin credentials, so the HttpOnly guest cookie goes with every request.
- `X-Requested-With: vybe-web` on every unsafe request (POST, PUT, PATCH, DELETE).
- A problem response becomes an `ApiError` with the contract `code`. A network failure or timeout is
  `network_error`. `retryable` is true when the outcome is unknown or temporary.
- `newIdempotencyKey()` makes one key per user action. A retry of the same action sends the same key.

## Sign-in and accounts (Supabase Auth)

- Configuration: the app reads `/runtime-config.json` at run time:
  `{ "supabaseUrl": "https://<project>.supabase.co", "supabaseAnonKey": "<publishable key>" }`.
  The hosting serves this file next to the app. The publishable key is public; put no secret in the
  file. Without the file (404), sign-in is off and the app is guest-only.
- The Supabase session is in memory only (spec §11: no tokens in localStorage). A reload signs the
  player out; the landing page then asks to sign in again. supabase-js loads as a lazy chunk at the
  first sign-in.
- A signed-in player sends the access token as `Authorization: Bearer`. `createGuest` and
  `linkAccount` send only the guest cookie. When a token refresh fails, the request fails; it never
  falls back to the cookie.
- J3 (`src/app/auth/account-link.ts`): after a sign-in, an identity with a player signs in to that
  player. An identity without a player links the guest of this browser (`POST /accounts/link`, a new
  guest if there is none), so the player id, wallet and inventory stay the same. When the identity
  has a player and the browser has another guest, the player chooses; VYBE merges nothing.
- Deletion of a linked account needs a sign-in from the last 5 minutes (`amr`); else the page asks
  for the password again ("Verification required").
- Admin roles come from `app_metadata.vybe_role`. The page only hides what a role cannot use; the
  API checks every call.

Local sign-in without a Supabase project: start the test Auth server
(`E2E_AUTH_PORT=5290 node tests/e2e/fake-auth/server.ts`), start the API with
`Auth__Supabase__Authority=http://localhost:5290/auth/v1`, and the dev server with `E2E_AUTH_PORT=5290`.
The dev proxy then serves `/runtime-config.json` from that server. Create users with
`POST http://localhost:5290/__test/users` with the header `X-Test-Secret: vybe-e2e-test-only` (see `tests/e2e/fake-auth/server.ts`).

## Street Hoops on the API (`/arcade/street-hoops`)

1. Start creates a session (`POST /games/street-hoops/sessions`), downloads the session's ruleset
   (`GET /games/rulesets/{id}`, cached per id) and checks that its checksum matches the session and
   the config (`rulesetChecksum`). Only then it starts the clock (`POST .../start`): local time 0 is
   the moment that answer arrives, and the seed comes from it.
2. The run plays as in practice: the local simulator scores each shot at once.
3. At 60 s the client sends the trace with one `Idempotency-Key` per run and shows the server
   outcome: verified (score, V-Coins, XP, level), rejected (reason in words) or expired. A 202
   answer is polled with `GET /games/sessions/{id}`.
4. When the answer is lost (network error, timeout, 5xx), "Retry sending the run" reads the session
   first and sends the same key again only if the run is not final yet (spec §2 J1).

The tuning panel and seed field exist only in practice mode, so an online run always uses the
server's ruleset.

## Shop and wardrobe

- Buy buttons only hint (owned, level, funds); the server decides. Each purchase has one
  `Idempotency-Key` (`ActionKeys`) until its outcome is known.
- A lost purchase answer shows "Check and retry": the client reads inventory and wallet first and
  sends the same key again only when the item is not owned yet (spec §2 J1, E09).
- Item pictures that do not load show a placeholder and a notice; equipment does not change (spec §9).
- Equip sends the avatar revision; a `revision_conflict` reloads the wardrobe.

## Run

The dev server proxies `/api` to the API on `http://localhost:5246` (`proxy.conf.json`), so the
guest cookie is first-party. A deployment must serve the API under the same origin at `/api`.

```bash
pnpm install
pnpm --filter @vybe/e2e db:up    # PostgreSQL with the schema (Docker)
ConnectionStrings__Vybe="Host=localhost;Port=55432;Database=vybe;Username=vybe;Password=vybe" \
  dotnet run --project services/api/src/Vybe.Api --launch-profile http
pnpm --filter web dev            # http://localhost:4200
```

Other commands:

```bash
pnpm --filter web test           # unit tests (vitest)
pnpm --filter web typecheck      # ngc (templates included) + test files
pnpm --filter web build          # production build to dist/web (no tuning panel)
```

## Street Hoops practice (`/practice`)

The client-only prototype of Street Hoops (spec v1.3 §7). Use it to tune the provisional ruleset
values (spec §17 item 4) on desktop and on phones. It needs no API and gives no rewards.

- The ruleset comes from `packages/game-rules/fixtures/rulesets/street-hoops-v1.json`.
  The app verifies its `checksum` with `rulesetChecksum` before the first run.
- Each release records one shot event `{tMs, aimX, power}` with integer values.
- After each shot, the app scores the trace with `evaluate()` from `@vybe/game-rules`
  (the same reference simulator as the server). The HUD and the ball animation show that
  result. The animation never decides a result.

On a phone on the same network, serve over HTTPS. The checksum check uses Web Crypto,
which browsers allow only on HTTPS or localhost:

```bash
pnpm --filter web dev --host 0.0.0.0 --ssl
```

Open `https://<your-computer-ip>:4200/practice` on the phone and accept the self-signed certificate.

## Controls

| Action | Mouse | Touch | Keyboard |
|---|---|---|---|
| Aim (`aimX` −1000…1000) | Move the pointer across the court | Put the finger down and move it | Hold `←` `→` or `A` `D` |
| Charge power (0…1000) | Hold the button | Hold the finger | Hold `Space` or `Enter` |
| Shoot | Release | Lift the finger | Release the key |

Power rises from 0 to 1000 in one charge cycle, falls back in the next, and repeats.
The white ring on the floor shows where the ball lands with the current aim and power.
The green rectangle under the basket is the hit zone (`hitDx` × `hitDp`). The gold
rectangle is the perfect zone. A release with the ring inside a zone gets that result.

Faint lines across the floor mark power 200, 400, 600 and 800. The power meter has ticks
at the same values, so the player can read the basket's depth and charge to the matching
tick without the landing ring.

The result label, score and streak update when the ball reaches the rim (700 ms after the
release), not at the release. With "Reduce motion" on, they update at once.

A release less than `minGapMs` after the last shot is not recorded ("Too soon"), so the
trace always passes the simulator checks.

"Reduce motion" (top right; default from `prefers-reduced-motion`) removes the ball arc
and fall. The result label always shows the result as text.

## What to tune

The tuning panel shows only in dev builds (`ng serve`). Changes apply when the next run
starts. A blank field uses the fixture value. The panel refuses values that break the
simulator invariants (for example, `durationMs` must be a multiple of `segmentMs`).

| Value | Ruleset key | What it changes |
|---|---|---|
| Hit window | `hitDx`, `hitDp` | Size of the green zone. Larger = more hits. |
| Perfect window | `perfectDx`, `perfectDp` | Size of the gold zone. |
| Basket speed | `segmentMs` | Time between waypoints. Smaller = faster, jerkier basket. |
| Basket range | `xMin`, `xMax` | How far left and right the basket goes (court edge = ±1000). |
| Power range | `pMin`, `pMax` | How near and far the basket goes (ideal power). |
| Shot cadence | `minGapMs` | Minimum time between shots. `maxEvents` stays at the fixture value. |

Client-only feel values (they do not change the score and are not ruleset values):

- Charge cycle ms: time for power to go from 0 to 1000. Shorter = harder power timing.
- Landing ring: turn off to test play without the aim aid. The floor lines and meter
  ticks stay.
- Seed: leave blank for a random basket path, or enter a `uint32` to replay a path.

The panel also shows session stats for the current config: runs, shots, hit rate (perfects
count as hits), perfect rate, best score, the last seed and FPS. Stats count finished runs
and reset when a run starts with a different config. Use FPS to check the targets: 60 FPS
on desktop, 30 FPS on mobile.

To propose new values, copy the "Effective config" JSON from the panel. A changed value
is a rules change: it needs a new ruleset version and regenerated fixtures (see the root
README). Do not edit `street-hoops-v1.json` in place.

## Code

| File | Purpose |
|---|---|
| `src/app/api/api-client.ts` | Typed API client, `ApiError`, idempotency keys |
| `src/app/api/messages.ts` | Player-facing text for problem codes |
| `src/app/player/player-store.ts` | The calling player (`/me`) as a signal; lost-guest hint |
| `src/app/player/guards.ts` | Route guard: no player → `/play` (or `/` after a signed-in session); API down → `/` |
| `src/app/api/account-api.ts` | Linking, blocks, export, deletion and admin operations |
| `src/app/auth/*.ts` | Runtime config, Supabase session, J3 link flow, sign-in form |
| `src/app/settings/*.ts`, `src/app/admin/*.ts` | Panels of `/settings` and `/admin` |
| `src/app/avatar/presets.ts` | Avatar presets from `assets/manifests/avatar.v1.json`, labels, 2D colors |
| `src/app/avatar/avatar-portrait.ts` | 2D fallback avatar portrait (SVG) |
| `src/app/pages/*.ts` | Screens of spec §3 |
| `src/app/ui/item-thumb.ts` | Item picture with a placeholder when it does not load |
| `src/app/street-hoops/online-session.ts` | API session flow: create, verify ruleset, start, submit, reconcile |
| `src/app/street-hoops/input.ts` | Quantizes pointer, touch and keyboard input to integer `aimX` and `power` |
| `src/app/street-hoops/trace.ts` | Shot gate (range, count, cadence), trace → `evaluate()`, session stats |
| `src/app/street-hoops/ruleset.ts` | Loads and verifies the fixture, applies tuning overrides |
| `src/app/street-hoops/court.ts` | Maps units to court positions; ball end point from the simulator result |
| `src/app/street-hoops/hoops-scene.ts` | Three.js scene with explicit mount and dispose |
| `src/app/street-hoops/street-hoops.ts` | Gameplay screen (practice and online modes): game loop, input, HUD, tuning panel |

The gameplay screens load as lazy route chunks, so Three.js is not in the initial bundle.
