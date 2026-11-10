# ADR 0007: Account policies (age gate, guest lifetime, profile visibility, Auth user deletion)

- Status: accepted
- Date: 2026-11-07
- Spec: v1.4 changelog items 1, 2, 6 and 8; `docs/decisions/` 1, 2, 6 and 8 (signed 2026-11-07)
- Code: stream J

## Context

The product owner signed decisions 1 (adults only, neutral age gate), 2 (sliding guest lifetime and
cleanup), 6 (profiles for signed-in players, private toggle) and 8 (J4 deletes the Supabase Auth user).
This ADR records how the API, the schema and the web app apply them.

## Decision

1. **Age gate in `createGuest`.** `POST /guests` needs `{birthYear, birthMonth}`. A missing or impossible
   month (after the current month, more than 120 years ago, month outside 1–12): 422 `validation_failed`.
   The rule is conservative (orchestrator review of #35): the API accepts only when the month of the 18th
   birthday is before the current UTC month. In that month itself the person can still be 17, so it is
   refused. Refused: 403 `age_not_eligible`, no player, no cookie, nothing stored or logged. Otherwise the
   player row gets `age_confirmed_at`, `age_policy_version` (`AgePolicy.Version`) and `age_band = 'adult'`.
   The month and the year are never stored. A CHECK makes the three columns all set or all NULL. Linking
   (J3) changes only `kind` and `auth_subject`, so the result stays. The adult age is a constant now;
   per-country values come with the counsel country list.
2. **Why the API checks the date, not the client.** The rule stays in one place and can change per country
   without a client release. The web app never shows the accepted dates.
2a. **The gate comes before any Supabase Auth user.** On sign-up without a guest, the web app passes the gate
   and creates the guest first (`AccountLink.prepareSignUp`), then calls the Supabase sign-up. So an
   under-age answer never creates an Auth user or an email record, and the guest cookie survives the
   email-confirmation reload. One path remains: a sign-in of an existing Auth user without a player. There
   the gate runs at the link, and the web app sends `createGuest` with that bearer token. If the answer is
   under age, the API queues that Auth user in the J4 deletion queue (item 7), and the web app signs out.
2b. **No guard for players without a gate result.** Players created before the migration have NULL age
   columns. Production has no players yet, and the deploy runbook requires the migration before the first
   player. `createGuest` is the only code path that creates a player, and it always writes the gate result
   (tested). A runtime guard ("no new content until the gate is passed") would need a second gate endpoint
   for existing players and would only protect development databases, so it is not built.
3. **Sliding guest expiry without a new column.** A session expires `min(last use + 30 days, player created
   + 90 days)`. The guest authentication handler renews `expires_at` when the last renewal
   (`expires_at − 30 days`) is before today's UTC midnight and the new expiry is later: one compare-and-swap
   `UPDATE` per session and day. The renewed cookie is written in `Response.OnStarting`, so an error
   response keeps it, and a response that clears the cookie (linking, deletion) wins. `players.created_at`
   is the cap base; `createGuest` writes it from the application clock.
4. **`GET /me` returns `guestExpiresAt`** (newest unrevoked, unexpired session; null for registered players)
   and `profileVisibility`.
5. **Guest cleanup reuses J4.** `GuestCleanup` (hourly, `Jobs:GuestCleanupSeconds`) marks every guest player
   that has at least one session, and whose sessions all ended (`LEAST(revoked_at, expires_at)`) more than
   30 days ago, as `deleting`. It queues a `deletion_requests` row (random UUID) in one statement with
   `SKIP LOCKED`. A suspended guest with an open report waits until a moderator resolves the report. The privacy processor
   then anonymizes it like any J4 request, and the deletion log copies the request for restores.
6. **Profiles.** `GET /profiles/{handle}` stays `AllowAnonymous` so that an anonymous caller gets the 404 of an
   unknown handle, not a 401. A private profile answers 404 to everybody except its owner. The handler
   writes the 404 itself (the exception handler would clear headers), so every answer has
   `Cache-Control: private, no-store` and `X-Robots-Tag: noindex`. `PATCH /me/privacy {profileVisibility}`
   sets `players.profile_visibility` (`players` default, or `private`). `POST /challenges` with the
   `recipientHandle` of another player's private profile is 404 `not_found` (one filter in
   `ChallengeEndpoints`). Accepted risk: blocks and reports still accept a private handle, so their answers
   tell a signed-in player that the handle exists. A private player must stay reportable and blockable
   (ADR 0005, decision 6 notes).
7. **Auth user deletion is a queue, not a step of the anonymization.** The anonymization transaction inserts
   the auth subject into `vybe.auth_user_deletions` (before it clears `players.auth_subject`). After the
   commit, the privacy processor calls `DELETE {Auth:Supabase:Authority}/admin/users/{id}` with
   `Auth:Supabase:ServiceRoleKey` (headers `apikey` and `Authorization: Bearer`). Success, or Auth's own
   404 `user_not_found`, deletes the row. Any other answer keeps the row with exponential backoff (1 minute,
   doubling, at most 6 hours). So an Auth outage never blocks or undoes the anonymization. No transaction or
   row lock is held during the HTTP call: the worker claims a row (moves `next_attempt_at` forward by a
   5-minute lease and commits), calls Auth, then records the result. The first failure ends the Auth step
   of a pass, so an outage costs one timeout per pass. The user id must parse as a UUID, so the request path
   cannot change, and the client follows no redirects. Without the key (Development, integration tests),
   the rows wait. `ProductionConfiguration` requires the key and an https Authority.
8. **The key and the user id never reach logs or traces.** Log messages carry only the attempt count. The
   Supabase HttpClients have no request loggers (`RemoveAllLoggers`), `Redaction` removes the segment after
   `/admin/users/`, and the OpenTelemetry HttpClient spans store the redacted URL. A test reads every log
   line of a deletion and finds neither the id nor the key.
9. **Anonymization also clears the age-gate result.**

## Consequences

- A contract change: `createGuest` has a required body, and `Me` has two new required fields. Clients
  older than this change cannot create guests.
- The service-role key is a new production secret with full access to the Supabase project
  (`docs/runbooks/deploy-and-rollback.md`, `docs/runbooks/secret-rotation.md`).
- A restore brings back both `auth.users` (same Supabase database) and the queue rows, so the queue and the
  deletion replay (ADR 0006) delete restored Auth users again.
- Drafts for counsel: `docs/legal/privacy-notice-draft.md`, `docs/legal/terms-support-draft.md`.
- Open: per-country ages and countries (counsel), and the backup retention to state in the notice.
- Accepted risks: a self-declared gate can be retried with another date (nothing remembers an under-age
  answer, by design); signed-in players can probe public handles (the per-player read limit applies).
