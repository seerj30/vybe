# ADR 0005: Account linking, profiles and safety

- Status: accepted
- Date: 2026-11-06
- Spec: [v1.3 §2 J3, §4 invariants 8 and 12, §5 Identity and Safety](../spec/VYBE_Technical_Specification_v1.3.md); contract: [ADR 0001](0001-openapi-contract.md)

## Context

Stream B of `docs/roadmap.md` adds J3 account linking, public profiles, blocks and reports on top of
the API slice (ADR 0003) and challenges (ADR 0004). This ADR records the decisions that the spec
leaves open.

## Decision

1. **Link proof.** `ILinkProofVerifier` checks the `linkProof`. The production implementation
   (`SupabaseLinkProofVerifier`) validates it as a Supabase access token with the bearer scheme's
   issuer, audience, algorithms, clock skew and signing keys (from the issuer metadata). It rejects
   the proof (422 `invalid_link_proof`) when the token is not valid, when `is_anonymous` is missing or
   `true`, when `email` is empty, or when `user_metadata.email_verified` is not `true`. Supabase must
   keep "Confirm email" on, so that it issues no session before the email is confirmed; the claim
   check is the second guard. The tests use the real verifier with the test signing key of `ApiFactory`.
2. **Link transaction.** `POST /accounts/link` authenticates with the guest cookie only, and checks
   `X-Requested-With` itself (the CSRF middleware skips requests with a bearer header). One
   transaction locks the player row (`FOR NO KEY UPDATE`), refuses an identity that any player
   already has (409 `identity_already_linked`, also for a concurrent link: the unique index on
   `auth_subject` decides), sets `auth_subject` and `kind = registered`, and revokes every guest
   session of the player. The response clears `__Host-vybe_guest`. Nothing is merged.
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
   cannot pass the limit. The limit is 429 `rate_limited`.

## Consequences

- The Supabase project settings ("Confirm email" on, anonymous sign-ins allowed or not) are part of
  the deployment (stream D).
- A linked player signs in with the bearer token; the old guest cookie is anonymous from then on.
