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

1. **Age gate in `createGuest`.** `POST /guests` needs `{birthYear}`. Missing or impossible year (in the
   future, more than 120 years ago): 422 `validation_failed`. A player who does not reach 18 in the current
   UTC calendar year: 403 `age_not_eligible`, no player, no cookie, nothing stored. Otherwise the player row
   gets `age_confirmed_at`, `age_policy_version` (`AgePolicy.Version`) and `age_band = 'adult'`. The birth
   year is never stored. A CHECK makes the three columns all set or all NULL (players created before the
   gate have NULL). Linking (J3) changes only `kind` and `auth_subject`, so the result stays. The adult
   age is a constant now; per-country values come with the counsel country list.
2. **Why the API checks the year, not the client.** The rule stays in one place and can change per country
   without a client release. The web app never shows the accepted years.
3. **Sliding guest expiry without a new column.** A session expires `min(last use + 30 days, player created
   + 90 days)`. The guest authentication handler renews `expires_at` when the last renewal
   (`expires_at − 30 days`) is before today's UTC midnight and the new expiry is later: one compare-and-swap
   `UPDATE` per session and day. The renewed cookie is written in `Response.OnStarting`, so an error
   response keeps it, and a response that clears the cookie (linking, deletion) wins. `players.created_at`
   is the cap base; `createGuest` writes it from the application clock.
4. **`GET /me` returns `guestExpiresAt`** (newest unrevoked, unexpired session; null for registered players)
   and `profileVisibility`.
5. **Guest cleanup reuses J4.** `GuestCleanup` (hourly, `Jobs:GuestCleanupSeconds`) marks every guest player
   whose sessions all ended (`LEAST(revoked_at, expires_at)`) more than 30 days ago as `deleting` and
   queues a `deletion_requests` row (random UUID) in one statement with `SKIP LOCKED`. The privacy processor
   then anonymizes it like any J4 request, and the deletion log copies the request for restores.
6. **Profiles.** `GET /profiles/{handle}` stays `AllowAnonymous` so that an anonymous caller gets the 404 of an
   unknown handle, not a 401. A private profile answers 404 to everybody except its owner. The handler
   writes the 404 itself (the exception handler would clear headers), so every answer has
   `Cache-Control: private, no-store` and `X-Robots-Tag: noindex`. `PATCH /me/privacy {profileVisibility}`
   sets `players.profile_visibility` (`players` default, or `private`). `POST /challenges` with the
   `recipientHandle` of another player's private profile is 404 `not_found` (one filter in
   `ChallengeEndpoints`). Blocks and reports still accept a private handle.
7. **Auth user deletion is a queue, not a step of the anonymization.** The anonymization transaction inserts
   the auth subject into `vybe.auth_user_deletions` (before it clears `players.auth_subject`). After the
   commit, the privacy processor calls `DELETE {Auth:Supabase:Authority}/admin/users/{id}` with
   `Auth:Supabase:ServiceRoleKey` (headers `apikey` and `Authorization: Bearer`). Success, or Auth's own
   404 `user_not_found`, deletes the row. Any other answer keeps the row with exponential backoff (1 minute,
   doubling, at most 6 hours). So an Auth outage never blocks or undoes the anonymization. The user id must
   parse as a UUID, so the request path cannot change. Without the key (Development, integration tests),
   the rows wait. `ProductionConfiguration` requires the key and the Authority.
8. **The key and the user id never reach logs or traces.** Log messages carry only the attempt count.
   `Redaction` removes the segment after `/admin/users/`, and the OpenTelemetry HttpClient spans store the
   redacted URL.
9. **Anonymization also clears the age-gate result.**

## Consequences

- A contract change: `createGuest` has a required body, and `Me` has two new required fields. Clients
  older than this change cannot create guests.
- The service-role key is a new production secret with full access to the Supabase project
  (`docs/runbooks/deploy-and-rollback.md`, `docs/runbooks/secret-rotation.md`).
- A restore brings back both `auth.users` (same Supabase database) and the queue rows, so the queue and the
  deletion replay (ADR 0006) delete restored Auth users again.
- Drafts for counsel: `docs/legal/privacy-notice-draft.md`, `docs/legal/terms-support-draft.md`.
- Open: the birth-year rule (a person born late in the year can be 17), per-country ages and countries
  (counsel), and the backup retention to state in the notice.
