# P1 design: friends and weekly leaderboard (stream O)

- Spec: [§4 `friendships`, invariants 10 and 12, §5, §16 P1](../spec/VYBE_Technical_Specification_v1.3.md); decisions
  [1 (age)](../decisions/01-age-policy.md), [6 (profile visibility)](../decisions/06-profile-visibility.md);
  ADRs [0004](../adr/0004-challenges-and-jobs.md), [0005](../adr/0005-accounts-and-safety.md), [0007](../adr/0007-account-policies.md)
- Status: built in stream O. Values marked *provisional* are safe defaults for the beta.

## Scope

1. Friends: request, accept, decline, cancel, remove; lists of friends and pending requests; the relation to one
   handle; challenge a friend from the list.
2. Weekly leaderboard per game (`gameId`): ISO week in UTC, best verified score per player, a global top 100, a
   friends board and the caller's own rank.

## Data model (one migration `FriendsAndLeaderboard`)

**`friendships`**: `id uuid PK`, `requester_player_id uuid FK`, `addressee_player_id uuid FK`,
`status text CHECK IN ('pending','accepted','declined')`, `suppressed bool NOT NULL DEFAULT false`,
`created_at` (time of the last request), `responded_at NULL`, `updated_at`.

- `CHECK(requester_player_id <> addressee_player_id)`, `CHECK(NOT suppressed OR status = 'pending')`.
- Canonical pair unique: `UNIQUE INDEX ux_friendships_pair ON (LEAST(requester, addressee), GREATEST(requester, addressee))`.
  One row per pair in every state, so a pair has exactly one relation.
- Indexes `(requester_player_id, status)` and `(addressee_player_id, status)` for the lists.

**`friend_request_counters`**: `(player_id, day) PK`, `sent int`. A durable daily send counter, updated with one
atomic `INSERT … ON CONFLICT DO UPDATE … RETURNING` in the request transaction (same pattern as
`player_daily_counters`). A cancel does not give the request back, so cancel-and-resend cannot pass the limit.

**`weekly_leaderboard_scores`**: `(game_id, week_start, player_id) PK`, `best_score int`, `session_id uuid FK`,
`achieved_at timestamptz`. `week_start` is the Monday (UTC date) of the ISO week of the run's `started_at`
(the same clock as `reward_day`). Index `(game_id, week_start, best_score DESC, achieved_at)`.

The board reads this summary table, never `game_sessions`. The verification transaction writes it with one upsert
that keeps the higher score (`ON CONFLICT … DO UPDATE … WHERE excluded.best_score > best_score`). The upsert locks
one row of the verifying player only, so invariant 10 holds. Cost on Supabase Nano: one small row per player and
week; the top 100 is an index range scan; "my rank" is one indexed count.

## Friends rules

| Action | Rule |
|---|---|
| Request `POST /me/friend-requests {handle}` | Under `PairLock` (invariant 12), then the block re-check. Reverse `pending` request (not suppressed) → the pair becomes friends at once. Already friends → 409 `already_friends`. Own pending request → 200 again (idempotent). |
| Accept `POST /me/friend-requests/{handle}/accept` | Only the addressee of a visible `pending` request; else 404 `not_found`. Under `PairLock`, block re-check. |
| Decline `POST /me/friend-requests/{handle}/decline` | Addressee only; idempotent 204. The row stays `declined`. |
| Cancel `DELETE /me/friend-requests/{handle}` | Requester only; deletes a `pending` row; idempotent 204. |
| Remove `DELETE /me/friends/{handle}` | Either friend; deletes the `accepted` row; idempotent 204. |
| Lists `GET /me/friends`, `GET /me/friend-requests` | Only the caller's own relations. No endpoint shows another player's friends. |
| Relation `GET /me/friendships/{handle}` | `none`, `friends`, `outgoing`, `incoming`. An unknown handle is `none` (no existence check). |

**Blocks.** A block (either direction) deletes the pair's `friendships` row in the block transaction, under the
pair lock (extends invariant 8). With a block in place:

- The caller blocked the target → 422 `recipient_blocked` (the caller knows it already).
- The target blocked the caller → the API answers as for a normal request (`pending`) and stores the row with
  `suppressed = true`. The target never sees it; it can never be accepted. Same privacy rule as a suppressed challenge.
- Accept of a request across a block → 404 `not_found`.

**Decline.** A declined request leaves the requester's outgoing list. The same requester can send a new request to
the same player after 30 days (*provisional*); before that → 409 `friend_request_declined`. The player who declined
can send a request at any time; it replaces the row.

**Profile privacy (decision 6).**

- A private player cannot receive new requests: 404 `not_found`, the same as an unknown handle. Exception: when the
  private player already sent a request to the caller, the caller's request accepts it (the private player asked).
- A private player can send requests. The addressee sees the requester's handle, display name and level.
- Friend lists show a private friend (the friendship is a mutual choice) with `profileVisible: false`; the web
  app then shows no profile link. Nobody can read another player's friend list, so a private player never appears
  to strangers through friends.
- A friend can challenge a private friend (`POST /challenges` with `recipientHandle`). Before, a private recipient
  was always 404.

**Guests.** Guests may use friends, because the existing rule lets guests use every social feature: they create and
accept challenges, see profiles (signed-in callers include guests, decision 6) and block and report. Guest
cleanup and J4 anonymization delete all friendship rows, counter rows and board rows of the player.

**Suspension.** A suspended player cannot send requests (403 `account_suspended`, as for challenge creation). Accept,
decline, cancel, remove and the lists still work.

**Abuse limits (*provisional*).**

| Limit | Value | Answer |
|---|---|---|
| New requests per player per UTC day (`friend_request_counters`) | 20 | 429 `rate_limited` |
| Open outgoing requests per player | 50 | 409 `friend_request_limit` |
| Friends per player (checked for both players at accept) | 200 | 409 `friend_limit_reached` |
| Re-request after a decline | 30 days | 409 `friend_request_declined` |
| List size | 200 friends, 100 newest incoming, 50 outgoing | no paging needed |

## Weekly leaderboard rules

`GET /leaderboards/{gameId}/weekly?scope=global|friends&week=YYYY-Www` (signed-in callers; `Cache-Control: private, no-store`).

- `gameId`: any game with a ruleset (`street-hoops` now; stream P's game works without an API change). Unknown → 404 `not_found`.
- `week`: ISO week (`2026-W46`), default the current UTC week; not before 2026-W01, not after the current week → 422 `validation_failed`.
- A run counts when: the session is `verified`; it is reward-eligible (minimum shots and full-length run, spec v1.4
  §7, the same rule as quests and challenges); it is not a challenge recipient run (that run replays a known seed);
  it has no `review_flags`; the player is not suspended (the verification already refuses rewards then).
- Excluded on read: deleting and deleted players, players with an active suspension. A suspension that ends brings
  the entry back. Admin action on a flagged player is a suspension, so flagged players leave the board.
- Global board: top 100, without private profiles. Friends board: the caller and the caller's accepted friends,
  private friends included (same reason as the friend list).
- Order: `best_score` descending, then `achieved_at` ascending, then player id. Rank is competition rank
  (equal scores share a rank: 1, 2, 2, 4).
- `me`: the caller's own score and rank in that scope, also when the caller is not in the top 100 or is private
  (`listed: false`; the rank counts listed players with a higher score). `null` when the caller has no counted run.

## Web

- `/friends`: add by handle, incoming (accept, decline), outgoing (cancel), friends (profile, challenge, remove).
  "Challenge" opens Street Hoops with `to={handle}`; after the run the existing challenge flow presets the recipient.
- `/leaderboard`: tabs Global and Friends, week label, own rank card, table of entries. Game picker when the API
  lists more than one game later; now Street Hoops only.
- Entry points: hub card (friends and leaderboard), "Friends" in the nav, "Add friend" on `/profile/:handle`
  (state from `GET /me/friendships/{handle}`).

## E2E cases (Playwright, `tests/e2e/specs/friends.spec.ts`)

- E13: player A sends a request to B from B's profile; B accepts on `/friends`; both see each other in the list.
- E14: A and B are friends; A blocks B from B's profile; the friend is gone for both; B's new request does not
  reach A.
- E15: a player makes a new verified run; `/leaderboard` shows the score and "my rank"; the friends tab shows the friend.

## Open product questions

1. Should a private player appear on the friends' leaderboard and friend lists? Default here: yes, friends only.
2. Should a block also hide the blocked player from the blocker's global leaderboard view? Default here: no (the
   board is a ranking; hiding rows would change rank numbers).
3. Limits: 20 requests per day, 50 open requests, 200 friends, 30-day re-request wait after a decline. Keep?
4. Should guests be able to use friends, or only registered players? Default here: guests too (same as challenges).
5. Should challenge recipient runs (known seed) ever count for the board? Default here: no.
6. Notifications for new requests (badge, email) are not in scope. Wanted for P1?
7. Should the board keep past weeks visible forever, or only the last N weeks? Default here: any week since 2026-W01.
