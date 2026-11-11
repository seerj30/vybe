# P1 second mini-game: Street Juggle

Status: design for stream P (spec v1.3 §16, P1 "second mini-game"). Engineering decided the values
below. They are provisional, like the Street Hoops v1 values. Open product questions are at the end.

## 1. Summary

Street Juggle is a 60-second football keep-up game. The ball falls on one of three street spots
(left, centre, right). The player taps the spot when the ball is at knee height. A good touch kicks
the ball up again, and the ball flies to a new spot. A late or wrong touch is a "whiff". A ball that
touches the ground is a "drop"; a new ball falls after a short pause. Gravity increases every
12 seconds, so the juggle gets faster and lower.

- One input: "touch spot N". One thumb (tap the left, centre or right third of the screen) or the
  keyboard (`←` `↓` `→`, or `A` `S` `D`; `Space` touches the centre spot).
- The player's avatar stands in the street and moves to the touched spot. The 2D figure uses the
  skin tone and hair color of the player's avatar.
- Same architecture as Street Hoops: server session, seed after the start call, event-only input
  trace, deterministic replay in C# (server) and TS (client), golden fixtures that both replay.

### Originality

The game uses only generic genre elements (timing window, combo streak, increasing speed). It does
not copy a commercial game: no name, levels, characters, art or sound from another game. The look
is the VYBE street style (night street, sodium lamps, chalk spots on the asphalt). The working name
"Street Juggle" follows "Street Hoops"; product can rename it (open question 1).

## 2. Rules (ruleset `street-juggle-v1`)

### 2.1 Input units (normative, all integers)

- `lane` in [0, `lanes` − 1]: 0 = left, 1 = centre, 2 = right.
- `tMs` in [0, `durationMs` − 1]: milliseconds since the start call, as in Street Hoops.
- One event = one touch: `{ "tMs": int, "lane": int }`. Exactly these two fields. An empty trace is
  legal and scores 0.

### 2.2 Ruleset config

| Key | Value | Meaning |
|---|---|---|
| `streamId` | 77 | PCG32 stream selector, uint32 |
| `durationMs` | 60000 | game time |
| `graceMs` | 5000 | upload allowance; never extends game time |
| `earlyToleranceMs` | 2000 | clock tolerance for `submitted_too_early` |
| `tickMs` | 10 | simulation tick (100 Hz); `durationMs`, `respawnMs`, `whiffLockMs`, `levelMs` are multiples |
| `lanes` | 3 | number of spots |
| `serveY` | 90000 | height of a new ball (height unit: 1/100,000 of the play height) |
| `kickY` | 28000 | ideal touch height (knee) |
| `goodDy` | 18000 | touch window: `|y − kickY| ≤ goodDy` |
| `perfectDy` | 5000 | perfect window: `|y − kickY| ≤ perfectDy` |
| `gravityStart`, `gravityStep` | 18, 3 | gravity per tick² at level 0, added per level |
| `levelMs` | 12000 | level = `tMs div levelMs` (levels 0–4) |
| `launchMin`, `launchMax` | 1300, 1700 | upward speed (height units per tick) after a kick |
| `respawnMs` | 1000 | pause after a drop before the next ball |
| `whiffLockMs` | 450 | "off balance" time after a whiff; touches in it are whiffs |
| `goodPoints`, `perfectPoints` | 100, 150 | points per touch |
| `starPercent`, `starPoints` | 15, 100 | chance of a gold ball per flight; bonus for touching it |
| `streakStart`, `streakStep`, `streakMax` | 3, 25, 100 | streak bonus, as in Street Hoops |
| `minGapMs` | 150 | minimum gap between touches |
| `maxEvents` | 400 | maximum touches (400 × 150 ms = 60 s) |
| `minEventsForRewards` | 5 | minimum touches for rewards, quests and challenges |
| `minRunMsForRewards` | 58000 | decision 4: `durationMs − earlyToleranceMs` |
| `minScoreForCoins` | 500 | decision 4: base coins need this score |

The client gets `config` from `GET /games/rulesets/street-juggle-v1` and never hard-codes it.

### 2.3 Simulation

State: `ball` (in play or waiting), `ballLane`, `y`, `vy`, `star`, `respawnTick`, `lockUntilTick`,
`streak`, `score`. Initial state: ball waiting, `respawnTick = 0`.

For each tick `k = 0 … durationMs / tickMs − 1`, in this order:

1. **Serve.** If the ball is waiting and `k ≥ respawnTick`: draw `ballLane` in [0, `lanes` − 1],
   then draw `r` in [0, 99] and set `star = r < starPercent`; set `y = serveY`, `vy = 0`.
2. **Touch.** If an event has `tMs div tickMs = k` (at most one, because `minGapMs ≥ tickMs`), score
   it (§2.4).
3. **Physics.** If the ball is in play: `g = gravityStart + gravityStep × (k × tickMs div levelMs)`;
   `vy = vy − g`; `y = y + vy`. If `y ≤ 0`: a **drop**. `streak = 0`, the ball waits,
   `respawnTick = k + 1 + respawnMs / tickMs`.

### 2.4 Touch scoring

A touch at tick `k` on `lane` is a **kick** when all of these are true:

- `k ≥ lockUntilTick` (the player is not off balance);
- the ball is in play, `lane = ballLane` and `vy ≤ 0` (falling);
- `dy = |y − kickY| ≤ goodDy`.

Else it is a **whiff**: `streak = 0`, `points = 0`, and when `k ≥ lockUntilTick`,
`lockUntilTick = k + whiffLockMs / tickMs` (a whiff in the lock does not extend it).

A kick is **perfect** when `dy ≤ perfectDy`, else **good**. Then `streak = streak + 1` and
`points = (perfect ? perfectPoints : goodPoints) + streakBonus(streak) + (star ? starPoints : 0)`,
with `streakBonus` as in Street Hoops: `min(streakMax, streakStep × (streak − streakStart + 1))`
when `streak ≥ streakStart`, else 0. The kick launches the ball: draw `vy` in
[`launchMin`, `launchMax`], then `ballLane` in [0, `lanes` − 1], then `r` in [0, 99] for `star`.
`y` does not change (the ball leaves from the touch height).

There are no other draws. The draw order is therefore: serve `lane, r`; kick `vy, lane, r`.

### 2.5 Result

`{ outcome: "verified", score, eventCount, maxStreak, kickCount, perfectCount, dropCount, touches }`.
Each `touches` item: `{ tMs, lane, ballLane, ballY, result, points, streak }`, with `result` ∈
`perfect`, `good`, `whiff`; `ballLane = −1` and `ballY = 0` when the ball is waiting.

### 2.6 Feel

With these values a flight takes about 1.7 s at level 0 and 1.0 s at level 4. The touch window is
about ±90 ms; the perfect window is about ±25 ms. A scripted player with a random timing error
(40 seeds per row, `packages/game-rules/src/juggle-autoplay.ts`) gives:

| Timing error (SD) | Median score | Kicks per run | Perfect share | Drops per run |
|---|---|---|---|---|
| 0 ms (bot) | 12,550 | 48 | 100 % | 0 |
| 30 ms | 10,900 | 46 | 74 % | 2 |
| 50 ms | 9,525 | 44 | 49 % | 3 |
| 80 ms | 5,850 | 34 | 37 % | 10 |
| 120 ms | 3,850 | 24 | 29 % | 16 |

So a casual player passes the 500-point coin floor, and the coin cap (150 at 6,500 points) needs a
good run, as in Street Hoops. Earlier values (`kickY` 20000, `goodDy` 15000, gravity 22) gave a
novice (80 ms) a median of about 700 points, which is too hard; `goodDy` 24000 gave a sloppy
player (120 ms) 4,500 points, which is too easy.
The `practice` route has a dev tuning panel, as Street Hoops has, to tune the feel before the beta.

## 3. Determinism plan

- **Integer math only.** All values are int32; the largest is `serveY` plus one launch (< 2^31).
  No floats, no division in the tick loop except `div` by positive constants (C# `/` and TS
  `Math.trunc` agree for non-negative operands).
- **Fixed tick.** 10 ms. An event applies at tick `tMs div tickMs`, before the physics of that tick.
- **Seeded RNG.** The same PCG32 as Street Hoops (spec §7), `initstate = seed`,
  `initseq = streamId`, `lo + next() mod (hi − lo + 1)`. The draw count depends on the trace (one
  draw set per serve and per kick), so the replay must process the trace in order. Both
  implementations share one `StreetJuggleSim` design: `advanceTo(tick)` and `touch(tMs, lane)`.
  The client uses the same class for live play, so live feedback equals the replay.
- **Golden fixtures** in `packages/game-rules/fixtures/street-juggle/{scoring,rejections}/*.json`,
  generated from the TS reference by `pnpm --filter @vybe/game-rules generate:fixtures`. The C# tests
  replay every file (`GoldenFixtureTests`). The ruleset file
  `fixtures/rulesets/street-juggle-v1.json` carries the RFC 8785 checksum, as Street Hoops does.

## 4. Server checks (anti-cheat limits)

The API runs the Street Hoops check order (spec §7) with the Street Juggle units:

1. `created` → rejected `not_started`.
2. `expired` or `submittedAt > submitDeadline` → `expired`.
3. `rulesetId` differs → rejected `ruleset_mismatch`.
4. (API, raw JSON tokens) an event that is not an object with exactly `tMs` and `lane` as strict
   integers → rejected `invalid_event`. A Street Hoops event `{tMs, aimX, power}` is also
   `invalid_event`.
5. `tMs` outside [0, 59999] or `lane` outside [0, 2] → rejected `out_of_range`.
6. More than 400 events → rejected `too_many_events`.
7. `tMs` not strictly increasing → rejected `non_monotonic`.
8. A gap below 150 ms → rejected `cadence_violation`.
9. `submittedAt < startedAt + maxT − earlyToleranceMs` → rejected `submitted_too_early`.

The 16 KB body cap holds: 400 events of `{"tMs":59999,"lane":2},` are 9.6 KB.

**Rewards (decision 4).** The same reward code as Street Hoops, keyed by the ruleset config: a
verified run is reward-eligible only with at least `minEventsForRewards` touches and
`submittedAt − startedAt ≥ minRunMsForRewards`; base coins need `score ≥ minScoreForCoins`. Coin and
XP formulas, the daily run cap and the XP cap are shared by all games (one `runs_rewarded` counter
per player and day, not per game).

**Spam guard.** Tapping all three spots does not pay: each wrong spot is a whiff, which resets the
streak and locks touches for 450 ms, so the right touch is usually lost too.

**Review flags.** `perfect_rate_high` (perfect share of touches above 90 % in a run of 30+ touches;
a skilled human is near 75 %, §2.6), `timing_spread_low` and `daily_cap_streak` apply.

`timing_spread_low` (added 2026-11-11): over the touches that kicked the ball (30 or more), the population
standard deviation of the signed error `(ballY − kickY) / goodDy` is under 0.08. A bot that replays the seed
and aims at a fixed "good, not perfect" offset avoids `perfect_rate_high` but has an almost constant error.
Simulation over 120 seeds (`autoplay` with per-touch offsets): human-like timing with σ 15–60 ms and any
bias stays at 0.10 or more (5th percentile); bots with a fixed offset (0, +30, +50, −40 ms) stay at
0.024–0.043, and a fixed offset with ±10 ms jitter at 0.075 or less. A bot that alternates or randomizes its
offset widely still passes: the flag raises the cost of a bot, it does not stop one. Flagged runs stay
verified and rewarded, and the weekly leaderboard leaves them out. The Street Hoops variance flags do not apply: the seeded flights make
the gaps vary for bots too. Flags only, no automatic action (decision 4).

**Limit.** As for Street Hoops, a replay proves that a trace is legal, not that a human played it.

## 5. API, contract and data

- New route `POST /games/street-juggle/sessions` (`createStreetJuggleSession`), same body and
  responses as `createStreetHoopsSession`. With `challengeId`, the challenge must be a Street Juggle
  challenge, else 404 `challenge_unavailable`.
- `start`, `submit` and `GET /games/sessions/{id}` stay generic; the session's `gameId` selects the
  rules. A `Games` registry maps `gameId` to config type, event parser, simulator and review flags.
- `Ruleset` becomes a `oneOf` with discriminator `gameId` (`StreetHoopsRuleset`,
  `StreetJuggleRuleset`).
- No database change: `game_sessions.game_id` and `game_rulesets.game_id` are free text already.
- Daily quests: `play_3`, `score_1500` and `challenge_or_streak` (max streak ≥ 5) count reward-eligible
  runs of either game. No code change was needed.
- Challenges: the challenge stores the creator run's ruleset and seed, so a Street Juggle run can
  start a challenge. The web links the recipient to the right game by `gameId`.

## 6. Web

- Routes: `/arcade/street-juggle` (online, needs a player) and `/practice/street-juggle` (offline,
  dev tuning panel in dev builds). Both lazy-load the game chunk. 2D canvas, no Three.js, so the chunk
  stays small.
- The hub play card and `/arcade` become a game picker with both games.
- Accessibility: the canvas has a label and the three spots are also real buttons for screen-reader
  and switch users; results go to an `aria-live` region; color is never the only signal (a gold ball
  also has a star shape). "Reduce motion" (and `prefers-reduced-motion`) removes the ball trail,
  screen shake and the avatar dash animation.
- Tab hidden: a practice run pauses (its clock moves by the hidden time). An online run cannot pause,
  because the server clock runs and `submit_deadline` is fixed at start; on return the game catches
  up and says "The run clock kept running while the tab was hidden" (open question 3).
- The figure fits its column; its kicking foot rises to the touch height, so the kick meets the ball
  where the rules judge it. The game chunk is about 10 kB (gzip) plus the shared rules chunk.

## 7. Tests

- **Rules (TS and C#):** PCG32 vectors (shared), hand-checked unit tests of serve, physics, touch
  window, whiff lock, streak, star and level; every golden fixture in both languages.
- **API (real PostgreSQL):** a valid run is verified and pays coins; a tampered trace is refused
  (lane 3 → `out_of_range`; a Street Hoops event shape → `invalid_event`; an edited trace with
  equal `tMs` → `non_monotonic`); a run shorter than 58 s is verified with 0 coins and
  `run_too_short`; Street Hoops sessions and fixtures are unchanged; a Street Hoops challenge
  cannot open a Street Juggle session.
- **E2E (Playwright):**
  - S1: guest → hub → pick Street Juggle → full run → "Run verified by VYBE" with a score, coins and
    XP; the hub wallet shows the coins.
  - S2: a run with fewer than 5 touches is verified and says why it pays no coins.
  - S3: the picker shows both games, and `/arcade/street-juggle` needs a player.
  - A tampered trace (lane 3) is refused with "a touch was out of range" and pays nothing.
  - The spot buttons and the keyboard record touches in practice.

## 8. Open questions (product)

1. **Name.** "Street Juggle" is a working name. Keep it, or choose another?
2. **One daily run cap for all games.** Now 10 full-coin runs per day across both games. Should
   each game have its own cap (more coins per day, see decision 5)?
3. **Pause.** The server clock runs while the tab is hidden. Do we want a server-side pause (needs a
   new endpoint and a session state change)?
4. **Quests.** Should quests name a game (for example "play 3 Street Juggle runs") to promote the new
   game?
5. **Tuning.** The values in §2.2 need a prototype test with real players before the beta.
