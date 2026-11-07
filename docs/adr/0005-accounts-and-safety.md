# ADR 0005: Account linking, profiles, safety, outfits, privacy and admin

- Status: accepted
- Date: 2026-11-06
- Spec: [v1.3 §2 J3, §4 invariants 8 and 12, §5 Identity, Safety, outfits, account and Admin](../spec/VYBE_Technical_Specification_v1.3.md); contract: [ADR 0001](0001-openapi-contract.md)

## Context

Stream B of `docs/roadmap.md` adds J3 account linking, public profiles, blocks, reports, saved outfits
and J4 data export and deletion on top of the API slice (ADR 0003) and challenges (ADR 0004). This
ADR records the decisions that the spec leaves open.

## Decision

1. **Link proof.** `ILinkProofVerifier` checks the `linkProof`. The production implementation
   (`SupabaseLinkProofVerifier`) validates it as a Supabase access token with the bearer scheme's
   issuer, audience, algorithms, clock skew and signing keys (from the issuer metadata). Then it reads
   the user record from the validated issuer's Auth endpoint (`GET {iss}/user`, with the proof and the
   publishable key `Auth:Supabase:ApiKey`, required outside Development). The proof is rejected (422
   `invalid_link_proof`) when the token is not valid, the record's `id` is not the token subject, the
   user is anonymous, or the record has no `email` or no `email_confirmed_at`. Token claims such as
   `user_metadata.email_verified` are not used: the user can change them. 401/403/404 from Auth reject
   the proof; 429, 5xx, a network error or a timeout (10 s) is 503 `auth_unavailable` with
   `Retry-After` (Auth's value, else 30 s), and the guest session stays valid. On an unknown signing
   key id the verifier asks the configuration manager for a refresh (rate-limited by the manager) and
   validates once more. The tests use the real verifier with the test signing key of `ApiFactory` and
   a fake Auth endpoint (`FakeSupabaseAuth`).
2. **Link transaction.** `POST /accounts/link` authenticates with the guest cookie only, and checks
   `X-Requested-With` itself (the CSRF middleware skips requests with a bearer header). Attempts are
   limited per client address (`RateLimits:LinkPerIpPerMinute`, default 20; IPv6 per /64) and per guest
   player (`RateLimits:LinkPerPlayerPerMinute`, default 5) → 429 `rate_limited` with `Retry-After`. The
   local token check runs first, then a local check that no player has the subject (409), and only
   then the call to Supabase Auth, so known identities and bad tokens never reach Auth. One
   transaction locks the player row (`FOR NO KEY UPDATE`), refuses an identity that any player
   already has (409 `identity_already_linked`, also for a concurrent link: the unique index on
   `auth_subject` decides), refuses a player whose deletion request committed meanwhile (401), sets
   `auth_subject` and `kind = registered`, and revokes every guest session of the player. The response clears `__Host-vybe_guest`. Nothing is merged.
3. **Profiles.** `GET /profiles/{handle}` returns `handle`, `displayName`, `level` and the avatar
   presets and equipment. Deleting and deleted players are 404 `not_found`. A block does not change
   the answer: a 404 for the blocked caller only would tell that caller about the block, because
   everyone else still sees the profile. `Cache-Control: private, no-store` stays, as in the contract.
4. **Blocks.** `POST /me/blocks` takes the pair lock (invariant 12), inserts the block
   (`ON CONFLICT DO NOTHING`, so a repeat is 204) and sets the pair's `open`/`accepted` challenges to
   `cancelled` in one transaction (invariant 8). Every challenge writer that involves the pair
   (create with a recipient, accept, rematch, recipient session) takes the same lock and re-checks
   blocks, so no live challenge survives a block. A challenge created after the recipient blocked the
   creator is stored `suppressed` (ADR 0004). A public invite of the blocker that nobody accepted has no
   recipient and stays `open`; the blocked player cannot accept it (404 `challenge_unavailable`).
   `DELETE /me/blocks/{handle}` is 204 also for an unknown handle and restores nothing.
5. **Reports.** `POST /reports` allows 10 reports per reporter in a rolling 24 hours (provisional,
   spec §5). The count and the insert run under the reporter's player row lock, so concurrent reports
   cannot pass the limit. The limit is 429 `rate_limited`. A reporter has at most one `open` report per
   target: a repeat returns 202 with that report's id.
6. **Saved outfits.** Save takes the player row lock, picks the lowest free slot (1–3) and copies the
   current `equipped_json`; `UNIQUE(player_id, slot)` is the database guard (409 `outfit_limit_reached`).
   An item is invalid when it is not owned, not active, in another slot than the outfit puts it, or not
   compatible with the current body preset. Apply checks the avatar revision, then every item, and
   writes nothing when one is invalid (422 `outfit_items_invalid` with `itemIds`); the revision trigger
   makes the write a compare-and-swap.
7. **Data export.** `POST /me/data-export` inserts a `queued` row; the partial unique index decides a
   race (409 `export_in_progress` with `exportId`). A player gets one ready export per rolling 24 hours
   (429 `rate_limited`). `PrivacyProcessor` builds the JSON document (`PlayerDataExport`: the player's own
   data with the block privacy rules; reports against the player and the free-text suspension reason,
   which can name reporters, stay out) and writes it through `IExportStorage`. `LocalExportStorage`
   (a directory, `file:` URL) is registered only in Development; elsewhere the API does not start until
   production storage is registered (stream D). Each `downloadUrl` is valid for
   min(10 minutes, export expiry), and `downloadExpiresAt` is that URL's expiry; the export itself
   expires after `Privacy:ExportDownloadHours` (default 24, provisional). Only the owner gets the URL.
   **Lock order:** every privacy job locks the player row first and export rows after it. The export job
   reads the player `FOR SHARE` (it waits for a running anonymization), then claims the export row with
   `FOR UPDATE SKIP LOCKED`; an export of a `deleting` or `deleted` player fails with `player_deleted`.
   **Files:** the storage key is derived from the export id, so a retried export replaces its file. A
   `failed` export that still has a `storage_key` is a recorded pending file deletion: the job deletes
   the file, then clears the key, and retries on the next pass while the storage fails. Once per hour an
   orphan sweep lists the stored files and deletes those whose export is unknown or `failed` without a
   key (a file written by a transaction that never committed).
8. **Deletion request.** A bearer caller needs a newest `amr[].timestamp` at most 5 minutes old (403
   `reauthentication_required`; a token without `amr` timestamps fails). The request runs through the
   idempotency protocol; one transaction sets `status = deleting`, inserts the `deletion_requests` row
   with `id = Idempotency-Key` and revokes the guest sessions; the response clears the cookie. A second
   request with another key while one is pending answers 202 with the pending `requestId`. A key that
   another player's request already uses as its id is 422 `idempotency_key_mismatch`. The key becomes
   an unauthenticated capability, so it must be a non-nil UUID v4 (else 422 `invalid_idempotency_key`).
   A replay of a completed request with the same key is answered before the reauthentication check.
   `GET /deletion-requests/{requestId}` needs no authentication.
9. **Anonymization** (ADR 0002 #11), one transaction per request in `PrivacyProcessor`: handle →
   `d_` + 18 random `[a-z0-9]`, display name, auth subject and suspension fields cleared,
   `status = deleted`, avatar reset to the default presets with no equipment, saved outfits and blocks
   (both directions) deleted, report `details` cleared (reporter or target), the player's `open`/`accepted`
   challenges cancelled, every export not yet failed set to `failed`/`player_deleted` (keeping its
   key, so the file deletion of #7 runs and retries), guest sessions revoked. The deletion never waits
   for the storage. The ledger, sessions and quests stay under the pseudonymous id. The
   freed Supabase identity can link a new guest. `PrivacyWorker` runs the processor every
   `Privacy:PollSeconds` (default 10) unless `Jobs:Enabled = false`.
10. **Admin.** `/api/v1/admin/*` authenticates with the Supabase bearer scheme only. The role is
    `app_metadata.vybe_role` (`moderator` or `administrator`): only the Supabase service role can
    write `app_metadata`, while `user_metadata` is user-writable and is never read for roles. An
    administrator may also do every moderator action; a moderator cannot adjust wallets, change the
    catalog or read the audit log (spec §15). Every write checks `reason` (3–500 characters) and adds
    the `admin_audit_logs` row in the same transaction (actor subject and role, action, entity,
    before/after JSON). Reports are listed oldest first (a queue); the audit log newest first, with exact
    filters. Suspension and lift lock the player row and refuse `deleting`/`deleted` players (404).
    A wallet adjustment runs through the idempotency protocol (actor = admin subject) with ledger key
    `admin:{Idempotency-Key}` and the audit row as the ledger reference. `delta` is a non-zero integer
    of at most 1,000,000 V-Coins in either direction (provisional; contract `minimum`/`maximum`), and the
    balance arithmetic is `checked`. A negative result is 409 `insufficient_funds`. The handler locks the
    wallet before it looks for the ledger key, so concurrent uses of one key serialize. The idempotency
    record is per admin, the ledger key per player: an existing `admin:{key}` row is replayed only when its
    audit row has the same actor and the same delta (an idempotency record that expired); otherwise the
    key was reused, which is 422 `idempotency_key_mismatch` (the spec §4 code for a reused key).

## Consequences

- The Supabase project settings ("Confirm email" on, anonymous sign-ins allowed or not) are part of
  the deployment (stream D).
- A linked player signs in with the bearer token; the old guest cookie is anonymous from then on.
- Linking calls Supabase Auth once per request; an Auth outage makes linking fail with 500.
- Supabase sessions of a deleted player are not revoked by the API; after anonymization the token has no
  player (404 `player_not_found`). Revoking them with the Supabase admin API is a stream D task.
- While `deleting`, a bearer caller can still use the other endpoints until the worker runs (seconds).
- Retention of `deletion_requests` and of game traces of deleted players follows spec §11 (stream D).
- Production export storage must delete objects after `Privacy:ExportDownloadHours` (for example a
  bucket lifecycle rule); `LocalExportStorage` keeps files until the player is deleted.
- Admin roles are granted in Supabase (`app_metadata.vybe_role`, service role or dashboard), not by the API.
- Accepted risk: the role is read from the token only, so a revoked role stays usable until the access
  token expires. Keep the Supabase JWT lifetime short (at most 1 hour, the default); for an urgent
  revocation, also sign the admin out in Supabase. Re-reading `app_metadata` on every admin write would
  need the Supabase service key in the API and is not done.
- Accepted risk: deletion clears an active suspension, and the freed identity can link a new guest. A
  suspended player can already start over as a new guest; the admin audit log keeps the suspension.
- Accepted risk (stream J, decision 6): `POST /me/blocks` and `POST /reports` accept the handle of a
  private profile, so their 204/202 tells a signed-in player that the handle exists, while
  `GET /profiles/{handle}` answers 404. A private player must stay blockable and reportable, so this stays.
