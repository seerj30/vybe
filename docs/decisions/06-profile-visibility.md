# Decision 6: Public profile default visibility and privacy controls

- Spec: [§1](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§3 `/profile/:handle`, `/settings`](../spec/VYBE_Technical_Specification_v1.3.md#3-screen-inventory), [§5 `GET /profiles/{handle}`](../spec/VYBE_Technical_Specification_v1.3.md#identity--avatar), [§17 item 6](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off); [ADR 0001 interpretations](../adr/0001-openapi-contract.md)
- Signs off: product owner and privacy counsel
- Needed before: stream B ships public profiles (stream B is in progress now)

## Question

1. Who can see a player's profile by default: anyone, signed-in players only, or nobody?
2. Which controls does a player get in `/settings`?

## Why it matters

- **The contract is public today.** `GET /profiles/{handle}` has optional authentication and returns `handle`, `displayName`, `level`, avatar presets and equipment (ADR 0001: "Spec §17 decision 6 (profile visibility) can still restrict this"). Anonymous callers can read it.
- **No setting exists.** `vybe.players` has no visibility column. Spec §3 already lists the states "private" and "unavailable" for `/profile/:handle`, and "privacy" in `/settings`.
- **Profiles start challenges.** "A challenge from a profile designates that profile's player as the recipient" (§2 J2); `POST /challenges` takes `recipientHandle` (§5). A private profile must decide whether it can still receive a profile challenge.
- **Handles are guessable.** Handles are short (`[a-z0-9_]{3,20}`), so an open profile endpoint lets anyone list players by trying handles. Rate limits help but do not stop it.
- **Minors.** If [decision 1](01-age-policy.md) allows players under 18, counsel can require high-privacy defaults for them (for example under the UK Age Appropriate Design Code).
- **Block privacy.** A blocked player must not learn about the block (§2 J2). A profile that a block hides must give the same 404 `not_found` as an unknown handle.

## Data on a profile today

| Field | Personal data? | Note |
|---|---|---|
| `handle` | yes (identifier the player chose) | can contain a real name; filtering is stream B |
| `displayName` | yes | free text, 40 chars |
| `level` | low | |
| Avatar presets and equipped items | low | shows purchases |
| Not shown: email, wallet, inventory list, ledger, reports, blocks | — | private by spec §1 |

## Options

| | Option | Effect | Effort (rough) |
|---|---|---|---|
| **A** | **Public by default** (current contract): anyone, also anonymous, with the handle | Easy sharing on social media. Highest exposure; profile scraping by handle. | Low: add `noindex`, rate limit, and a "private" toggle. 1–2 days. |
| **B** | **Players only by default**: any signed-in player (guest or registered) can see a profile; anonymous callers get 404. Toggle: "players only" or "private". | Sharing works inside VYBE and in challenge flows (the invitee becomes a guest first). Scraping needs a guest per IP (guest creation is rate limited, 10/min per IP). | 2–3 days: column, settings endpoint, 404 rules, web settings, tests. |
| **C** | **Private by default; opt-in to players only** | Lowest exposure. Profile challenges and profile links mostly do not work until players opt in. | Same as B; product loses a social surface. |
| **D** | **Contacts only**: visible to players who share a completed challenge | Good privacy, social link stays. | 4–6 days: query on `challenges`, index, tests. Friendships are P1, so this is the only "contact" signal in MVP. |

What "private" means in every option: `GET /profiles/{handle}` returns 404 `not_found` (same as unknown, so the state does not leak), the player cannot receive new profile challenges (the creator gets the same 404 as for an unknown handle), and challenge results still show the handle to the other participant.

## Recommendation

**Option B: signed-in players by default, with a "private" toggle in `/settings`. If decision 1 allows players under 18, make "private" the default for them.**

Reasons:

- It keeps the J2 social loop (profile challenges, sharing in the app) and removes anonymous access, which has no MVP use.
- It costs about the same as option C, but does not hide the main social surface.
- It is more conservative than the current contract. Counsel can relax it later; tightening a public default after launch is harder.
- Add `X-Robots-Tag: noindex` and keep `Cache-Control: private, no-store` (§5) on profile responses in all options.

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Column `players.profile_visibility text NOT NULL DEFAULT 'players' CHECK (profile_visibility IN ('players','private'))` | `services/api/src/Vybe.Data` new migration, `db/schema.sql` | stream B |
| `GET /profiles/{handle}`: require authentication (anonymous → 404), apply visibility and blocks | `packages/contracts/openapi/vybe-api.v1.yaml` (`getProfile` security), `Features/Profile*` | stream B |
| Settings endpoint, for example `PATCH /me/privacy {profileVisibility}`, and `profileVisibility` in `GET /me` | contract, ADR 0001 | stream B |
| `POST /challenges` with `recipientHandle` of a private profile → 404 `not_found` | `Features/ChallengeEndpoints.cs` | stream B |
| `/settings` privacy control; `/profile/:handle` "unavailable" state | `apps/web` | stream C successor |
| Spec §1, §3, §5, §17 item 6 resolved | spec (new version and changelog entry) | orchestrator |
| Privacy notice text on profiles | [decision 8](08-legal-wording.md) | counsel |
