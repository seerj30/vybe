# P1 design: advanced achievements (stream R)

- Spec: [§4 `achievements`, `player_achievements`, invariants 10, 11 and 12; §5; §7; §8; §16 P1](../spec/VYBE_Technical_Specification_v1.3.md);
  decisions [5 (economy)](../decisions/05-economy.md), [6 (profile visibility)](../decisions/06-profile-visibility.md),
  [8 (deletion)](../decisions/08-legal-wording.md); designs [friends and leaderboard](p1-friends-leaderboard.md),
  [second game](p1-second-game.md)
- Status: built in stream R. Values marked *provisional* are safe defaults for the beta.

## Scope

1. 29 achievements across both games, play habits, quests, challenges, friends, the weekly leaderboard and the
   collection. Three tiers: bronze, silver, gold.
2. The server computes all progress. The client never reports progress.
3. `GET /me/achievements` (all achievements with progress), badges on the public profile, and a list of new unlocks
   in the run submit result (for a toast).
4. A one-time backfill for existing players.

Not in scope: V-Coin rewards (decision 5, see open question 1), item rewards (`achievements.reward_item_id` stays
`NULL`), hidden achievements, notifications.

## Achievement list (`assets/manifests/achievements.v1.json`)

| Code | Name | Rule (stat ≥ target) | Tier |
|---|---|---|---|
| `hoops_first_run` | First Bucket | `runs:street-hoops` ≥ 1 | bronze |
| `hoops_score_1500` | Hot Hand | `best:street-hoops` ≥ 1,500 | bronze |
| `hoops_score_4000` | Swish Machine | `best:street-hoops` ≥ 4,000 | silver |
| `hoops_score_7500` | Court Legend | `best:street-hoops` ≥ 7,500 | gold |
| `juggle_first_run` | First Touch | `runs:street-juggle` ≥ 1 | bronze |
| `juggle_score_3000` | Keepy-Uppy | `best:street-juggle` ≥ 3,000 | bronze |
| `juggle_score_6500` | Ball Whisperer | `best:street-juggle` ≥ 6,500 | silver |
| `juggle_score_10000` | Never Drops | `best:street-juggle` ≥ 10,000 | gold |
| `runs_10` | Warming Up | `runs` ≥ 10 | bronze |
| `runs_50` | Regular | `runs` ≥ 50 | silver |
| `runs_250` | Street Veteran | `runs` ≥ 250 | gold |
| `all_games` | All-Rounder | `games_played` ≥ 2 | bronze |
| `streak_3` | Three in a Row | `play_streak` ≥ 3 | bronze |
| `streak_7` | Full Week | `play_streak` ≥ 7 | silver |
| `quests_10` | Quest Starter | `quests_claimed` ≥ 10 | bronze |
| `quests_50` | Quest Hunter | `quests_claimed` ≥ 50 | silver |
| `challenge_first` | Game On | `challenges_completed` ≥ 1 | bronze |
| `challenge_win_1` | First Win | `challenges_won` ≥ 1 | bronze |
| `challenge_win_10` | Rival Crusher | `challenges_won` ≥ 10 | silver |
| `rematch_1` | Run It Back | `rematches_completed` ≥ 1 | bronze |
| `friends_1` | New Friend | `friends` ≥ 1 | bronze |
| `friends_10` | Crew | `friends` ≥ 10 | silver |
| `weekly_top10` | Top Ten | `weekly_top10` ≥ 1 | silver |
| `weekly_top1` | Number One | `weekly_top1` ≥ 1 | gold |
| `items_1` | First Drip | `items_bought` ≥ 1 | bronze |
| `items_10` | Collector | `items_bought` ≥ 10 | silver |
| `items_30` | Closet Goals | `items_bought` ≥ 30 | gold |
| `outfit_1` | Fit Check | `outfits_saved` ≥ 1 | bronze |
| `outfits_3` | Wardrobe Full | `outfits_saved` ≥ 3 | silver |

Score targets come from the tuning tables: Street Hoops `score_1500` quest, XP cap at 5,600 and coin cap at 6,500
(decision 4); Street Juggle medians of 3,850 (sloppy), 5,850 (novice) and 10,900 (good) (p1-second-game.md §2.6).
All targets are *provisional*.

**Reward: badge and XP only.** Bronze 25 XP, silver 50 XP, gold 100 XP (*provisional*). All 29 give 1,350 XP
(14 bronze, 10 silver, 5 gold): less than two days of the 800 daily game XP cap. Achievement XP is not limited by the
daily game XP cap (same rule as quest XP, spec §8). **Effect on V-Coins:** XP raises the level, and every new level
pays 100 V-Coins (spec §8). 1,350 XP moves a new player from level 1 to level 4 sooner (300 V-Coins earlier) and a
level 20 player by at most one level. No achievement pays V-Coins directly. In practice the first verified run of a new
player now reaches level 2 at once (80 run XP + 25 XP for "First Bucket" ≥ 100), so the 100 V-Coin level reward comes
on run 1 instead of run 2. **Decided (product owner, 2026-11-11, `docs/decisions/README.md` P1 decisions): keep the
achievement XP, including the indirect level-up V-Coins; no direct V-Coin rewards.** Review with the decision 5 economy
review after 2 weeks of beta data.

## Why a manifest

Definitions are versioned content in `assets/manifests/achievements.v1.json`, the same pattern as `quests.v1.json`:

- Product can change names, targets and XP without a code change; the deploy `seed` step applies the file.
- `code` + `version` identify a definition. A code that leaves the file becomes inactive (`is_active = false`): it
  does not unlock any more, and is not listed. Earlier unlocks stay in the database.
- The rules stay in code: a definition only names a **stat** and a **target**. A new stat needs code; a new
  achievement on an existing stat needs only the file.
- The API loads the file at startup (`ContentCatalog.Achievements`) for names, tiers and XP, and the seeder writes
  `achievements` rows (`code`, `criteria = {"stat", "target"}`, `version`, `is_active`) for the foreign key of
  `player_achievements` and for the unlock query.

## Data model (one migration `Achievements`)

The spec §4 tables exist since the initial schema and have no writer yet.

**`achievements`** (exists, no rows yet): add `version int`, `reward_xp int CHECK (reward_xp >= 0)` and
`is_active bool` (all `NOT NULL`; the seeder sets them). `criteria` is `{"stat": text, "target": int ≥ 1}`. The unlock
pays the `reward_xp` of the row, so the transaction needs no manifest.

**`player_achievements`** (exists): `(player_id, achievement_id) PK`, `unlocked_at`. One row per unlock. A row is
never updated.

**`content_markers`** (new): `name text PK`, `fingerprint text`, `completed_at`. A finished one-time content job
(here: the backfill, see Backfill). No player data.

**`player_stats`** (new): `(player_id, stat) PK`, `value bigint NOT NULL CHECK (value >= 0)`, `updated_at`.
`CHECK (stat ~ '^[a-z0-9_:-]{1,64}$')`. One row per player and stat, written with one atomic upsert (invariant 11
pattern), never read-modify-write.

| Stat | Write | Where (existing transaction) |
|---|---|---|
| `runs`, `runs:{gameId}` | `+1` | run verification, reward-eligible run (same rule as quests and `runs_rewarded`) |
| `best:{gameId}` | `GREATEST(value, score)` | run verification, only when the run counts for the leaderboard (`LeaderboardEndpoints.Counts`: eligible, no `review_flags`, not a challenge recipient run) |
| `games_played` | `GREATEST(value, count of runs:* > 0)` | run verification |
| `play_streak` | `GREATEST(value, consecutive UTC days with runs_rewarded > 0 up to the run's reward day)` | run verification |
| `weekly_top10`, `weekly_top1` | `GREATEST(value, 1)` when the global rank is ≤ 10 / is 1 | run verification, after the leaderboard upsert |
| `challenges_completed`, `challenges_won`, `rematches_completed` | `GREATEST(value, count)` | `challenge-resolved` outbox job (both participants) |
| `quests_claimed` | `GREATEST(value, count)` | quest claim |
| `outfits_saved` | `GREATEST(value, count)` | outfit save |
| `friends` | `GREATEST(value, count of accepted friends)` | `achievements-sync` outbox job, queued by friend accept |
| `items_bought` | `GREATEST(value, count of purchased items)` | `achievements-sync` outbox job, queued by purchase |

`GREATEST` keeps the best value: a removed friend or a deleted outfit does not take an achievement away, and a write
that runs twice changes nothing. The `+1` writes run once per session: the session row lock and the
`active → verified` status change make the verification run once.

The weekly rank uses the global board rule: 1 + the number of listed players (not private, not deleting, deleted or
suspended) with a higher best score in the same game and week. Ties share a rank. The count stops at 10 (`LIMIT 10`),
so the verification transaction does O(10) work. A private player gets the own rank
too, as in the board's `me` field (the badge then shows only to that player). The rank is the rank at the time of
the run; a later better run of another player does not take the achievement away.

## Unlock rule

After every stat write, one statement unlocks what is due:

```sql
INSERT INTO player_achievements (player_id, achievement_id, unlocked_at)
SELECT s.player_id, a.id, now FROM achievements a JOIN player_stats s ON s.stat = a.criteria->>'stat'
WHERE s.player_id = :player AND a.is_active AND s.value >= (a.criteria->>'target')::bigint
ORDER BY a.code ON CONFLICT DO NOTHING RETURNING achievement_id
```

- Idempotent: the primary key stops a second unlock. A replay of a submit, a retried job or a second backfill
  unlocks nothing new and grants no XP twice.
- XP: the sum of the new unlocks' XP goes through `RewardService.AddXpAsync` (level and level coins as for quests).
- A suspended, deleting or deleted player unlocks nothing (spec §6: suspension blocks economic actions). Stats still
  record. The next unlock pass after the suspension (any later stat write) catches up. The `achievements-sync` job
  waits until the suspension ends, as the `challenge-resolved` job does.
- Only active definitions unlock.

## Locks (invariants 10 and 12)

No new lock order. Every write runs after the existing locks of its transaction, and locks only the rows of one
player: that player's `player_stats` rows, `player_achievements` rows, and (for a level-up) the wallet.

| Transaction | Existing order | Added |
|---|---|---|
| Run verification | player row → session row → counters, quests, wallet → leaderboard row | stats, unlocks, XP (player row held) |
| `challenge-resolved` job | outbox row → player row → counter, wallet, quest | stats, unlocks, XP |
| Quest claim | player row → quest row → wallet | stats, unlocks |
| Outfit save | player row → outfits | stats, unlocks |
| Friend accept, instant accept | pair lock → friendship row | inserts two `achievements-sync` outbox jobs (no player lock) |
| Purchase | wallet → ledger, inventory | inserts one `achievements-sync` outbox job (no player lock) |
| `achievements-sync` job (new) | outbox row → player row | derived counts for all stats of the table above that are counts, unlocks, XP |

Why a job for friends and purchases: friend accept holds the pair lock and a `FOR SHARE` lock on the other player;
an upgrade to `FOR NO KEY UPDATE` there can deadlock with a second accept for the same player. The purchase locks the
wallet before the player row is ever locked; a player row lock after it inverts the verification order (player →
wallet). The job takes the player row in its own transaction, in the existing outbox order. The unlock then comes a
few seconds later (outbox poll interval), which is acceptable for these achievements. Each event queues its own job
(no dedupe key: an upsert on a pending job row could wait on the row that the running job locks, and that job then
waits on the player and wallet rows). The volume is bounded by one purchase per item and the friend limits.

## API (contract `packages/contracts/openapi/vybe-api.v1.yaml`)

**`GET /me/achievements`** (signed-in; `Cache-Control: private, no-store`): read only.

```json
{ "unlockedCount": 3, "totalCount": 29, "earnedXp": 75,
  "achievements": [ { "code": "hoops_first_run", "name": "First Bucket", "description": "…",
    "category": "games", "tier": "bronze", "rewardXp": 25, "target": 1, "progress": 1,
    "unlockedAt": "2026-11-11T10:00:00Z" } ] }
```

`progress` is `min(stat, target)`. `earnedXp` is the XP of the unlocked achievements. Order: the manifest order
(category, then tier). Categories: `games`, `play`, `competition` (challenges and the weekly leaderboard), `social`,
`collection`.

**Public profile** `GET /profiles/{handle}`: new field `badges: [{ code, name, tier }]`, unlocked only, gold first.
The profile rules do not change (decision 6): signed-in callers only, a private profile only for its owner, every
other case is the 404 of an unknown handle. So a stranger never sees a private player's badges. Blocks do not change
the profile answer (ADR 0005: a different answer would tell the block), so they do not change the badges.
No `unlockedAt` on the public profile (data minimization: unlock times tell when a player was online).

**Run submit** (verified result and its replays): new field `achievementsUnlocked: [{ code, name, tier,
rewardXp }]`, empty when nothing unlocked. A replay of the same session answers the same list (unlocks with
`unlocked_at = verified_at` of that session, which only that transaction writes). `rewards.levelAfter` includes the
achievement XP.

## Backfill

Small data (beta): one computation in the deploy `seed` step (`release` runs it after `migrate`), after the achievement
seeder, **once per rules version**. A new table `content_markers (name PK, fingerprint, completed_at)` holds the row
`achievements-backfill` with the SHA-256 of the active rules (code, version, stat, target). The seed step compares it
with the manifest: equal → no backfill work; different or missing → backfill, then write the marker. The marker is
written only after the backfill completed, so a backfill that stopped half way (a failed deploy) runs again on the next
seed. Names, descriptions and XP are not in the fingerprint (they need no backfill). It also runs in Development and
tests through the same call. For each player that is not deleting or deleted (checked again after the row lock), in its
own transaction with the player row lock:

- `runs`, `runs:{gameId}`, `best:{gameId}`, `games_played`: from the player's verified `game_sessions`, with the
  same eligibility rule (`Rewards.Ineligibility` with the ruleset config) and the same leaderboard rule for `best`.
- `play_streak`: the longest run of consecutive days with `runs_rewarded > 0` in `player_daily_counters`.
- `weekly_top10`, `weekly_top1`: the player's rank in each stored week of `weekly_leaderboard_scores` (global rule).
- Every count stat: the same queries as the `achievements-sync` job.
- Writes with `GREATEST`, then the unlock pass. A second run changes nothing.

Backfilled unlocks give XP too (same rule for old and new players). `unlocked_at` is the backfill time.

## Privacy, deletion, export

- J4 deletion (`PrivacyProcessor`, also used by guest cleanup): deletes the player's `player_stats` and
  `player_achievements` rows in the anonymization transaction. The pseudonymous player row stays as before.
- Data export (`PlayerDataExport`): new lists `achievements` (`code`, `unlockedAt`) and `stats` (`stat`, `value`).
- No other player can read another player's progress. The profile shows unlocked badges only.

## Web

- `/achievements`: summary (unlocked of total, XP), filter chips (all, unlocked, in progress, locked), a list
  grouped by category. Each card: tier medal, name, description, progress bar (`role="progressbar"` with
  `aria-valuenow`/`aria-valuemax`), XP, unlock date. Mobile first, one column on a phone.
- No new nav item (the phone bar is full). Entry points: a hub card "Achievements" and a link on the own profile.
- `/profile/:handle`: a "Badges" section with the unlocked badges (medal and name); a link to `/achievements` on the
  own profile; nothing when there are no badges.
- After a run: a toast "Achievement unlocked: {name} (+{xp} XP)" for each new unlock (`role="status"`, polite).

## E2E cases (Playwright, `tests/e2e/specs/achievements.spec.ts`)

- E20: a new guest plays a full Street Hoops run → the result shows the "First Bucket" toast → `/achievements` shows it
  unlocked and the others locked → `/profile/{own handle}` shows the badge.
- E21: a second player (signed in) opens the first player's profile and sees the badge; after the first player sets
  the profile to private, the second player gets the "not found" page.

API tests (real PostgreSQL) cover: unlock on a verified run; no score achievement from a flagged run, a challenge
recipient run or a rejected run; idempotent replay (same list, no double XP); friends and purchase through the
outbox job; challenge win through the `challenge-resolved` job; the private profile hides badges from strangers;
J4 removes the rows and the export lists them; the backfill gives the same result as live play and runs twice
without change.

## Open product questions

1. **V-Coin rewards.** Should achievements pay V-Coins (for example 50 / 100 / 250 by tier)? Not built; decide in the
   decision 5 economy review after 2 weeks of beta data.
2. ~~**XP values.**~~ **Decided 2026-11-11 (product owner): keep** bronze 25, silver 50, gold 100 XP (1,350 in
   total), including the indirect level-up V-Coins. Review with the decision 5 economy review.
3. **Weekly top 10 in a small beta.** With few players, the top 10 is easy. Keep, or start it only above N players
   on the board?
4. **Flagged runs.** Flagged runs count for `runs` (as for quests) but not for score achievements. Should a confirmed
   bot lose achievements after an admin suspension? Default here: no automatic removal.
5. **Badges on the profile.** All badges, gold first. Should the player choose up to 3 badges to show?
6. **Item rewards.** The spec allows `reward_item_id` (an `achievement_grant` item). Wanted for P1, for example an
   exclusive cosmetic for `weekly_top1`?
