# ADR 0001: OpenAPI contract for API v1

- Status: accepted (draft contract `1.0.0-draft.1`)
- Date: 2026-11-06
- Spec: [v1.3 §5](../spec/VYBE_Technical_Specification_v1.3.md), changelog v1.3.1 to v1.3.3

## Context

Spec v1.3 §5 describes the API in prose. The ASP.NET Core server and the Angular client
need one machine-readable contract that both are built against, and that CI can check.

## Decision

1. **Contract first.** [`packages/contracts/openapi/vybe-api.v1.yaml`](../../packages/contracts/openapi/vybe-api.v1.yaml)
   (OpenAPI 3.1) is the source of truth. The server implements it; the TS types in
   `packages/contracts/src/generated/` are generated from it and committed.
2. **Tooling.** Redocly CLI (`recommended` rules) lints the file; openapi-typescript
   generates the types. `src/consistency.check.ts` fails `pnpm typecheck` if the contract's
   `StreetHoopsConfig` or `RejectionReason` drifts from `@vybe/game-rules`.
3. **Errors.** Every operation has a `default` response with `application/problem+json`, which
   covers the common codes of spec §5. Endpoint-specific codes are listed per HTTP status in
   the `x-problem-codes` operation extension. `ProblemCode` enumerates every code.
4. **Pagination.** `{ items, nextCursor }`; `nextCursor` is null on the last page.
   `limit` 1–100, default 20 (catalog default 24).
5. **Nullable fields are present.** Response fields that can be empty are required and
   `null` (not omitted), so clients handle one shape.

### Changes and additions to spec v1.3 (applied in the spec as v1.3.1)

| # | Change | Reason |
|---|---|---|
| 1 | `/challenges/by-token/{token}` → `/invites/{token}` (and `/accept`) | `by-token` matched `/challenges/{challengeId}/…`; routers and linters flag ambiguous paths. |
| 2 | `DELETE /admin/players/{id}/suspension` with body → `POST …/suspension/lift` | Many proxies and clients drop DELETE bodies; the audit `reason` must not be lost. |
| 3 | CSRF: cookie-authenticated unsafe requests need `X-Requested-With: vybe-web`; else 403 `csrf_check_failed` | Spec §11 required CSRF protection without a mechanism. A custom header forces a CORS preflight, which only the VYBE origin passes. No token state to manage. |
| 4 | 409 `export_in_progress` | The schema allows one `queued`/`running` export per player; the API needs a code for it. |
| 5 | 500 `internal_error` | Every `code` value must be enumerated. |
| 6 | `GET /me` adds `kind`, `status`, `suspendedUntil` | J1 ends with a link-account prompt for guests; the client must also show suspension and deletion state (§6). |
| 7 | `POST /guests` requires `X-Requested-With: vybe-web`; 409 `already_authenticated` | Without it, a cross-site form could replace a victim's guest cookie and their unlinked progress would be lost. |
| 8 | Deletion reauthentication uses the newest `amr[].timestamp`, not `iat` | Supabase refreshes renew `iat`, so an `iat` check passes for any active session. |
| 9 | `GET /deletion-requests/{requestId}` has no authentication | The deletion revokes the caller's sessions; the random UUID v4 `requestId` is the capability, and the response has no personal data. |
| 10 | Invite token = base64url(HMAC-SHA256(server secret, challengeId)) | Idempotent replay of `createChallenge` must return the same `shareUrl` without storing the raw token (spec §11: tokens only hashed). |
| 11 | 422 `invalid_link_proof` | Separates "proof rejected, guest still valid" from 401 "guest session gone" (spec §2: lost guest = lost progress). |
| 12 | Handles: lowercase in storage and responses (`Handle`); case-insensitive input (`HandleInput`) | Spec §4 format is lowercase; users may type capitals. |
| 13 | Idempotency records of `createChallenge` / `createRematch` omit `shareUrl`; replay rebuilds it | The generic protocol stores response bodies for 72 h; a stored `shareUrl` would put the raw token in the database. |
| 14 | `X-Requested-With` declared on all 21 unsafe guest-callable operations | Generated clients send only declared headers; per-route enforcement must not miss `linkAccount` or `requestAccountDeletion`. |
| 15 | `Challenge.shareUrl` for the designated recipient of an `open` challenge | Rematch and profile-challenge recipients otherwise cannot accept in the app (J2). |
| 16 | `requestAccountDeletion` takes `Idempotency-Key`, which becomes `requestId`; 409 `export_in_progress` carries `exportId` | A lost 202 must not lose the only status capability after sessions are revoked (J4). |
| 17 | 404 `player_not_found` for a Supabase identity without a player | "Login" with a new Supabase account must be distinguishable from an expired token. |
| 18 | `Cache-Control: private, no-store` on `getProfile`, `listCatalogItems`, `getChallengePreview` | Caller-dependent bodies (`owned`, block rules) must never be served from a shared cache. |

### Accepted risk

**Block inference.** A blocked player can still infer a block: by comparing the signed-in and
anonymous answers of `getChallengePreview`, or by seeing a challenge expire before its deadline.
A throwaway guest defeats any identity-based fix, and hiding block-cancelled challenges entirely
costs more than the benefit for the beta. Revisit if moderation reports show block evasion.

### Interpretations (no spec change)

- **Public operations:** `createGuest`, `getRuleset`, `getDeletionRequest`. **Optional auth:**
  `getProfile`, `listCatalogItems` (`owned` false when anonymous), `getChallengePreview`.
  **Admin:** bearer token only; required role in `x-required-role`.
- **`shareUrl`** is returned by `createChallenge` / `createRematch` (rebuilt on replay) and to the
  designated recipient by `getChallenge` / `listMyChallenges`. Because the token is derived, a
  later "copy link again" feature for creators needs no schema change.
- **`Idempotency-Key` and `X-Requested-With`** are validated by the server itself (428 / 422 /
  403 codes of spec §5), not by generic schema validation, so the listed `code` values hold.
- **Linking** accepts only non-anonymous Supabase identities with a confirmed email; the 200
  response clears the `vybe_guest` cookie.
- **Avatar reference data** (body, skin, face, hair, hair-colour presets; item id → sku, asset
  URL, thumbnail) comes from the versioned static asset manifest of spec §9 on the CDN, not
  from an API operation. The manifest version and `catalog_version` must match; the server
  validates preset ids and item ids against the same manifest (422 `invalid_preset`).
- **Seed** is returned only by `startGameSession`; `getGameSession` never includes it.
- **Challenge outcome** is `result: creator_win | recipient_win | tie | null`, not a winner
  player id; the preview shows `creatorScore` so the invitee knows the score to beat.
- **Submit body:** `events` items and `clientReportedScore` are free-form (`{}`) so the framework
  never returns 400 for event content (spec §5 exception); the server validates raw tokens.
- **Public profile fields:** `handle`, `displayName`, `level`, avatar presets and equipment.
  Spec §17 decision 6 (profile visibility) can still restrict this.

## Consequences

- A contract change needs `pnpm --filter @vybe/contracts generate` and a review of the
  generated diff; CI must fail when the generated file is stale (not set up yet).
- The C# server needs request validation that matches the schema constraints (formats,
  lengths, `additionalProperties: false`) and must return the listed `code` values.
- The common codes are not repeated per operation; the generated client types see them
  through the `default` response.
