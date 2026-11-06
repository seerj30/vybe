# ADR 0004: Challenges, background jobs and daily quests

- Status: accepted
- Date: 2026-11-06
- Spec: [v1.3 §2 J2, §4, §5 Challenges, §6, §7](../spec/VYBE_Technical_Specification_v1.3.md); slice: [ADR 0003](0003-api-vertical-slice.md)

## Context

Stream A of `docs/roadmap.md` adds J2 challenges on top of the P0 API slice. The spec fixes the
states, the invariants (5, 8–12) and the codes. This ADR records the decisions that the spec leaves open.

## Decision

1. **Invite tokens.** `ChallengeInvites` derives the token as base64url(HMAC-SHA256(secret, challenge id
   as text)), 43 characters. The database stores only SHA-256(token). The secret comes from
   `Challenges:InviteSecret` (base64, at least 32 bytes). Outside Development the API does not start
   without it. In Development a missing secret gives a random secret per process, so links stop
   working after a restart. Share links are `{Challenges:ShareBaseUrl}/challenge/{token}`. Outside
   Development, `Challenges:ShareBaseUrl` must be an absolute https URL, else the API does not start.
2. **Idempotent replays without the token.** `Idempotency.RunAsync` takes an optional presenter. It
   changes a stored success body on the way out, for the first response and for replays. Create and
   rematch store `{challengeId, acceptDeadline}` and the presenter adds `shareUrl`. The accept
   operation hashes the SHA-256 of the token, not the token.
3. **Visible status on reads.** Every read and filter shows `cancelled` as `expired`. Reads also apply
   the sweeper rules: an `open` challenge at or after `accept_deadline`, and an `accepted` challenge at
   or after `complete_deadline` without an `active`/`submitted`/`verified` recipient session, show as
   `expired` before the sweeper writes it.
4. **Preset recipient.** The "preset recipient is someone else" rule applies while the challenge is
   `open`. After an accept, another player gets 409 `challenge_already_accepted` (public invite) or
   the status answer, not 404. A suppressed challenge answers 404 `challenge_unavailable` to every
   identified player except its creator. An anonymous preview shows every known token.
5. **Recipient run.** Session creation with `challengeId` takes the pair lock, re-checks blocks, then
   locks the challenge row (`FOR UPDATE`), creates the session with the creator run's seed and ruleset
   and binds it by compare-and-swap. A preset recipient who has not accepted gets 404
   `challenge_unavailable`. `start` checks `complete_deadline` (410 `challenge_expired`) before the
   challenge status (404 `challenge_unavailable`). A recipient session that ends `rejected` or
   `expired` (submit, late submit, missed start deadline, or replaced by a new session) moves its
   challenge `accepted -> expired` in the same transaction.
6. **Outbox.** `OutboxProcessor` claims one due job at a time with `FOR UPDATE SKIP LOCKED` and runs
   it in one transaction, which also writes `done`. A failure rolls back and records `attempts`,
   `last_error` and `run_after = now + 2^attempts s` (at most 1 h); after 10 attempts the job is
   `failed`. `challenge-resolved` locks one player row only. It skips the winner bonus when the ledger
   row `challenge:{id}` already exists, so a retried job never pays twice. A deleting or deleted player
   gets no bonus and no quest progress, and the job ends. For a suspended player the job stays `pending`
   and runs again at `suspended_until` (an indefinite suspension: every day), without using an attempt,
   so the bonus and the quest progress arrive when the suspension ends. A job that becomes `failed` is
   logged at Error. The hosted `OutboxWorker` polls every `Jobs:OutboxPollSeconds` (default 5).
   `Jobs:Enabled = false` turns all workers off; the tests run the jobs directly. `Jobs:OutboxPollSeconds`
   and `Jobs:SweepSeconds` must be at least 1 (checked at start-up).
7. **Sweepers.** `Sweeper` runs every `Jobs:SweepSeconds` (default 30) in batches of 500. It claims
   rows with `FOR UPDATE SKIP LOCKED`, so it never waits for a request, and every write is a status
   compare-and-swap. Sessions: `created` after `start_deadline` and `active` after `submit_deadline`
   become `expired`, and their `accepted` challenge becomes `expired` in the same statement.
   Challenges: `open` at or after `accept_deadline`; `accepted` at or after `complete_deadline` only
   without a recipient session or with a `created`/`rejected`/`expired` one. The recipient session is
   read `FOR SHARE SKIP LOCKED`: when a start or submit holds it, the challenge waits for the next
   sweep, so a run that becomes `active` is never cut off. Players: an elapsed suspension is reset to
   `active` and its fields are cleared.
8. **Daily quests.** The three definitions are content in `assets/manifests/quests.v1.json`, seeded by
   `QuestSeeder` like the catalog (Development, tests, deploy step); a definition that is not in the
   file becomes inactive. The progress rules are code (`QuestProgress`); `criteria` documents them.
   Every progress write is the upsert of invariant 11 for the newest active daily version of a code.
   A reward-eligible run adds progress in the verification transaction on its `reward_day`; the
   `challenge-resolved` job adds `challenge_or_streak` on the UTC date of `resolved_at` for both
   participants (target 1, so a retry cannot overcount). `GET /me/quests` takes the player row lock,
   like every other quest writer, then creates the day's rows. A claim locks the player row, then the
   quest row; it pays coins (`quest:{id}`) and XP outside the daily gameplay cap, with level rewards.
   `claimableUntil` = `period_start` 00:00 UTC + 7 days; a claim at or after it is 410.

## Consequences

- The invite secret is a deploy-time secret (stream D). Rotating it breaks all open invite links.
- The production deploy step must seed quests as well as rulesets and catalog.
- Stream B (blocks) cancels `open`/`accepted` challenges under `PairLock`; the reads in this ADR already
  show those challenges as `expired`.
- Lock order for one player's writes: player row first (`FOR NO KEY UPDATE`), then session, quest and
  wallet rows. Create-session, submit, quest list and claim, and the outbox all follow it, so a session
  replaced during its submit cannot deadlock. Challenge paths take the pair lock and the challenge row
  before the player row; no transaction locks two players' rows.
