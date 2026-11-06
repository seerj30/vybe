# Street Hoops prototype (`apps/web`)

A client-only playable prototype of Street Hoops (spec v1.3 §7). Use it to tune the
provisional ruleset values (spec §17 item 4) on desktop and on phones. There is no
backend, no login and no persistence.

- The ruleset comes from `packages/game-rules/fixtures/rulesets/street-hoops-v1.json`.
  The app verifies its `checksum` with `rulesetChecksum` before the first run.
- Each release records one shot event `{tMs, aimX, power}` with integer values.
- After each shot, the app scores the trace with `evaluate()` from `@vybe/game-rules`
  (the same reference simulator as the server). The HUD and the ball animation show that
  result. The animation never decides a result.

## Run

```bash
pnpm install
pnpm --filter web dev            # http://localhost:4200
```

On a phone on the same network, serve over HTTPS. The checksum check uses Web Crypto,
which browsers allow only on HTTPS or localhost:

```bash
pnpm --filter web dev --host 0.0.0.0 --ssl
```

Open `https://<your-computer-ip>:4200` on the phone and accept the self-signed certificate.

Other commands:

```bash
pnpm --filter web test           # unit tests (vitest)
pnpm --filter web typecheck      # ngc (templates included) + test files
pnpm --filter web build          # production build to dist/web (no tuning panel)
```

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
| `src/app/street-hoops/input.ts` | Quantizes pointer, touch and keyboard input to integer `aimX` and `power` |
| `src/app/street-hoops/trace.ts` | Shot gate (range, count, cadence), trace → `evaluate()`, session stats |
| `src/app/street-hoops/ruleset.ts` | Loads and verifies the fixture, applies tuning overrides |
| `src/app/street-hoops/court.ts` | Maps units to court positions; ball end point from the simulator result |
| `src/app/street-hoops/hoops-scene.ts` | Three.js scene with explicit mount and dispose |
| `src/app/street-hoops/street-hoops.ts` | Gameplay screen: game loop, input, HUD, tuning panel |

The gameplay screen loads as a lazy chunk (`@defer`), so Three.js is not in the initial bundle.
