# ADR 0003: API vertical slice (P0)

- Status: accepted
- Date: 2026-11-06
- Spec: [v1.3 §5, §7, §16](../spec/VYBE_Technical_Specification_v1.3.md); contract: [ADR 0001](0001-openapi-contract.md); schema: [ADR 0002](0002-database-schema.md)

## Context

Spec §16 gates the P0 vertical slice on E01 (guest → avatar → verified game → wallet),
E02 (purchase → equip → reload), E05 (no double reward) and E06 (no negative balance).

## Decision

1. **ASP.NET Core minimal APIs** in `services/api/src/Vybe.Api`, one static class per feature
   (`IdentityEndpoints`, `GameplayEndpoints`, `EconomyEndpoints`). Handlers raise
   `ApiProblemException` with the contract's `code`; one exception handler writes RFC 9457
   problem details (`/problems/{code}`, `traceId`). Unknown errors become 500 `internal_error`
   without internal details.
2. **Authentication.** A policy scheme selects the Supabase JWT bearer scheme when an
   `Authorization: Bearer` header is present, else the guest-cookie scheme. Guest tokens are 256-bit
   random, stored as SHA-256 only, in a `__Host-vybe_guest` cookie (`Secure; HttpOnly;
   SameSite=Lax; Path=/`, no `Domain`, so a sibling subdomain cannot overwrite it) valid 30 days
   (provisional, §17 item 2). An expired or revoked cookie, or one of a `deleting`/`deleted`
   player, counts as anonymous. A Supabase identity without a player gets 404 `player_not_found`.
   Bearer tokens: pinned issuer and audience, ES256/RS256 only, 30 s clock skew; outside
   Development the app does not start without `Auth:Supabase:Authority` (or `MetadataAddress`).
3. **CSRF** is a middleware before authentication: unsafe cookie requests and `createGuest` need
   `X-Requested-With: vybe-web` (ADR 0001 #3, #14); the path is normalized (trailing slash).
   **Limits:** every request body is capped at 16 KB (Kestrel limit plus a `Content-Length` check →
   413 `payload_too_large`); `createGuest` is rate limited per client address (default 10 per
   minute, `RateLimits:GuestsPerIpPerMinute`) → 429 `rate_limited`. Behind a proxy, forwarded
   headers must be configured so the client address is real.
4. **Idempotency** (`Infrastructure/Idempotency.cs`) implements the spec §4 protocol: a 30 s lease
   claimed in its own statement, takeover of elapsed leases only while `in_progress`, and completion
   fenced on the lease inside the business transaction. Business 4xx outcomes are stored and
   replayed. Replays return the stored JSON, which `jsonb` may reformat (same content, key order
   may differ).
5. **Submit** reads the raw body (16 KB cap): shape errors are 400; event errors are the terminal
   rejection `invalid_event` (strict integer tokens). The C# simulator decides the outcome. A
   verified run pays rewards in the same transaction, locking only this player's rows
   (`FOR NO KEY UPDATE` on the player, the daily-counter upsert, `FOR UPDATE` on the wallet).
   Verification is synchronous, so the `202 submitted` response is not used yet. When the trace has
   an invalid event, checks 1–3 of spec §7 (`not_started`, `expired`, `ruleset_mismatch`) still run
   first, on an empty trace. A suspended, deleting or deleted player gets 403 `account_suspended`
   and no reward, even on a session opened before the suspension.
   **Session locks:** create-session locks the player row, so concurrent creations serialize;
   start locks the session row, so a repeated start returns the same 200.
6. **Content** (`assets/manifests/avatar.v1.json`, `catalog.v1.json`, rulesets from
   `packages/game-rules/fixtures/rulesets/`) is loaded at startup; a ruleset whose checksum does not
   match its config stops the start-up. Development and tests seed rulesets and catalog at start-up;
   production runs the same seeding as a deploy step.
7. **Time** comes from `TimeProvider`, so tests control the game clock. Database defaults (`now()`)
   are used only for audit timestamps and idempotency leases.

## Not in this slice

Challenges (`challengeId` → 404 `challenge_unavailable`), quests and quest progress in the
verification transaction, account linking, saved outfits, blocks and reports, admin, export and
deletion, per-player rate limits, the `idempotency_records` cleanup job, CORS (allow
`X-Requested-With` only from the web origin, never `AllowAnyOrigin` with credentials) and
security headers (CSP, HSTS). The contract and schema already cover the features.

## Consequences

- `GET /games/sessions/{id}` and a canonical submit replay report `levelAfter` as the player's
  current level; the level at verification time is not stored.
- `Vybe.Api.Tests` checks every response body against `#/components/schemas/*` of the contract
  (JsonSchema.Net, 2020-12). OpenAPI's `discriminator` is removed before validation because it is
  not a JSON Schema keyword; each `oneOf` member pins its own `const` status.
