# Decision 2: Guest session lifetime and account recovery limits

- Spec: [§2 J1 and J3](../spec/VYBE_Technical_Specification_v1.3.md#2-user-flows-and-failure-handling), [§4 `guest_sessions`](../spec/VYBE_Technical_Specification_v1.3.md#core-tables), [§17 item 2](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off); [ADR 0003 #2](../adr/0003-api-vertical-slice.md)
- Signs off: product owner and engineering lead; counsel reviews the recovery and support wording ([decision 8](08-legal-wording.md))
- Needed before: closed beta (beta players start their 30-day clock on day 1)

## Question

1. How long does a guest session live: 30 days, 90 days, or a sliding window?
2. What can a player recover, and what can support do, when a guest cookie or a login is lost?

## Why it matters

- **Guests lose everything when the session ends.** A guest has no email and no password. The only credential is the `__Host-vybe_guest` cookie. Spec §2 J1: "If guest cookie is lost: explain guest progress cannot be recovered without linked account."
- **The current lifetime is fixed, not sliding.** `GuestTokens.Lifetime = 30 days` (`services/api/src/Vybe.Api/Infrastructure/Authentication.cs:27`). `POST /guests` sets `expires_at = now + 30 days` (`Features/IdentityEndpoints.cs:50`). Nothing renews it. **A guest who plays every day loses the wallet, the inventory and the history on day 31**, unless the guest links an account first.
- **The client does not see the deadline after creation.** `POST /guests` returns `guestExpiresAt`, but `GET /me` does not. The web app cannot warn a returning guest without a contract change.
- **Expired guests stay in the database for ever.** No job removes or anonymizes players whose guest sessions expired. These rows are personal data with no owner who can ask for export or deletion ([decision 8](08-legal-wording.md)).
- **Safari can cut the cookie to 7 days.** Recent Safari versions shorten the lifetime of cookies that a server sets when the server's IP address does not match the web site's address (a tracking-prevention rule). If the API runs on a different host from the web app, Safari guests can lose progress after 7 days. Serve the API on the same origin as the web app (`/api` behind the same edge). The web dev server already does this (`apps/web/proxy.conf.json`). Verify with a Safari test on the real hosting setup ([decision 7](07-hosting.md)).
- **Security trade-off.** The guest token is a bearer credential. A longer lifetime gives a stolen cookie (for example from a shared device) a longer use. The cookie is `HttpOnly`, so page scripts cannot read it.

## Options

| | Option | Effect on an active guest | Effect on security and data | Effort (rough) |
|---|---|---|---|---|
| **A** | **30 days, fixed** (current code) | Loses all progress on day 31, even if active every day. | Shortest exposure. Expired guest data stays until a cleanup job exists. | None. |
| **B** | **90 days, fixed** | Loses all progress on day 91. | 3 times the exposure of A. | 0.5 day (constant, tests, spec). |
| **C** | **Sliding 30 days of inactivity, absolute cap 90 days** | Keeps progress while active, up to day 90. Strong link prompt before the cap. | Idle cookies end after 30 days. Active cookies end at 90 days. | 2–3 days: renew `expires_at` and the cookie at most once per day on an authenticated request; add `guestExpiresAt` to `GET /me`; warning UI; tests. |
| **D** | **Sliding 30 days of inactivity, no cap** | Never loses progress while active. | A cookie can live for ever. Less reason to link. | 2 days (as C without the cap). |

For every option, the recovery rules are the same:

| Case | Self-service recovery | Support can do |
|---|---|---|
| Guest cookie lost or expired | None. | Nothing safe: there is no proof of ownership. Support must not move progress to another player based on a claim. |
| Registered player, lost session | Log in again through Supabase Auth (email or OAuth). The player id stays the same. | Help with Supabase Auth problems only. |
| Registered player, lost access to the email account | None in MVP. | Only with a verification process that counsel approves. Recommend: no manual transfer in MVP. |
| Verified identity already linked to another player | None. Spec §2 J3: "do not auto-merge". | Explain that each identity has one player. No merge tool in MVP. |

## Recommendation

**Option C: sliding 30 days of inactivity, absolute cap of 90 days from guest creation.**

Reasons:

- Option A punishes the most engaged guests. Those are the players with the most to lose and the most reason to link.
- The cap keeps a reason to link and limits how long a stolen cookie works.
- Renewal at most once per day keeps the database writes low (one `UPDATE` per guest per day).
- Show `guestExpiresAt` in the hub. Show a link prompt when fewer than 14 days are left, and on every visit in the last 3 days.

Add a cleanup rule for expired guests in [decision 8](08-legal-wording.md) (recommend: anonymize a guest player 30 days after the last guest session expired, with the J4 anonymization of ADR 0002 #11).

Do not add support-assisted transfer of guest progress. There is no evidence of ownership, so a transfer would let anyone claim another guest's items.

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Replace the fixed lifetime with an idle window and an absolute cap (for example `Auth:GuestIdleDays = 30`, `Auth:GuestMaxDays = 90`) | `services/api/src/Vybe.Api/Infrastructure/Authentication.cs`, `Features/IdentityEndpoints.cs` | stream B |
| Renew `guest_sessions.expires_at` and re-send the cookie at most once per day; never past the cap. Use a column for the cap (for example `absolute_expires_at`) | `services/api/src/Vybe.Data` new migration, `db/schema.sql` | stream B |
| Add `guestExpiresAt` (null for registered players) to `GET /me` | `packages/contracts/openapi/vybe-api.v1.yaml`, regenerate types, ADR 0001 | stream B |
| Expiry warning and link prompt | `apps/web` hub and shell | stream C successor |
| Guest cleanup job (anonymize expired guests) | `services/api/src/Vybe.Api/Jobs/Sweeper.cs` or a new job | stream B, after [decision 8](08-legal-wording.md) |
| Same-origin API routing and a Safari cookie test | `infra/`, `tests/e2e` (WebKit project) | stream D |
| Spec text: §4 `guest_sessions`, §2 J1, §17 item 2 resolved | spec (new version and changelog entry) | orchestrator |
