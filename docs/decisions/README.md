# Sign-off pack: open decisions of spec §17

This pack prepares the eight decisions that [spec v1.3 §17](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off)
requires before public launch. Each file gives the question, the evidence in the repository,
2–4 options with effects and rough effort, one recommendation, the changes after sign-off, and
who signs off.

The pack changes no code, no spec text and no ADR. After a decision is signed, the orchestrator
updates the spec (new version and changelog entry) and gives the code changes to the stream that
owns the files (`docs/roadmap.md`).

Legal topics (decisions 1, 6 and 8) contain no legal advice. They list what the design does and
what counsel must check per country.

## Summary

| # | Decision | Recommendation | Signs off | Needed before | Code effort after sign-off |
|---|---|---|---|---|---|
| [1](01-age-policy.md) | Age, minors, countries | **Adults only (18+ / local age of majority)** with a neutral age gate before guest creation; small set of countries that counsel approves; teen access (16+) later as a separate project | product owner, counsel, DPO | closed beta | 3–5 days |
| [2](02-guest-sessions.md) | Guest lifetime, recovery | **Sliding 30 days of inactivity, absolute cap 90 days**; expiry warning; no support transfer of guest progress | product owner, engineering lead | closed beta | 2–3 days |
| [3](03-art-direction.md) | Art, licensing, ownership | **Contractor or studio under work-for-hire** for rig, bodies and items; one in-house pipeline owner; licensed packs only for neutral props | product owner, art lead, counsel | art production start | art: weeks; code: viewer and manifest |
| [4](04-street-hoops-tuning.md) | Street Hoops tuning | **Keep v1 gameplay values for the beta; fix the low-effort run** (full-length run and score ≥ 500 for coins); build `review_flags` with the starting thresholds | product owner, engineering lead | closed beta | 2–3 days |
| [5](05-economy.md) | Economy | **Keep earn rules; grow and reprice the catalog** (common 300–800, rare 1,500–3,000, epic 6,000–10,000; ≥ 30 items); review after 2 weeks of beta data | product owner | closed beta | content only |
| [6](06-profile-visibility.md) | Profile visibility | **Signed-in players only by default**, "private" toggle; private by default for minors if decision 1 allows them | product owner, counsel | stream B ships profiles | 2–3 days |
| [7](07-hosting.md) | Hosting, load, on-call | **Supabase Pro + one always-on container in the same region**, transaction pooler, same-origin `/api`; beta budget ceiling USD 150/month; load test at 20 rps; primary + backup on-call in business hours | product owner, engineering lead | closed beta (unblocks stream D) | stream D scope |
| [8](08-legal-wording.md) | Support, retention, deletion wording | **Pseudonymized ledger kept (if counsel accepts), retention table as draft, email support for beta**; conservative wording that says "anonymize" | counsel, DPO, product owner | closed beta (notice), public launch (final) | retention jobs, J4 Auth deletion |

## Findings from the evidence

These facts came up while the pack was written. They are not decisions, but the decisions above
depend on them.

1. **A 3-second run earns rewards.** Five shots of any result make a run reward-eligible; the run can be submitted 600 ms after start. 20 such runs per day pay 350 V-Coins, the same as an honest casual player ([decision 4](04-street-hoops-tuning.md), [decision 5](05-economy.md)).
2. **Earnings are about 4 times too high for the catalog.** A casual player owns the whole current catalog (7 priced items, 2,450 V-Coins) on day 9; a regular player on day 3 ([decision 5](05-economy.md)).
3. **The guest lifetime is fixed, not sliding.** An active guest loses all progress on day 31 (`Authentication.cs:27`), and `GET /me` does not return the expiry ([decision 2](02-guest-sessions.md)).
4. **Expired guests are never removed**, and J4 does not yet delete the Supabase Auth user ([decision 8](08-legal-wording.md)).
5. **`review_flags` has no writer.** The column exists; no code computes anti-automation flags ([decision 4](04-street-hoops-tuning.md)).
6. **Scale-to-zero hosting stops the background workers** (outbox, sweeper), and a separate API host can shorten the guest cookie in Safari ([decision 7](07-hosting.md)).

## Dependencies between decisions

| Decision | Depends on | Reason |
|---|---|---|
| 6 profile visibility | 1 age | minors need high-privacy defaults |
| 8 wording | 1, 2, 7 | age rule, guest cleanup, processors and region appear in the notice |
| 5 economy | 3 art, 4 tuning | the catalog size depends on art capacity; the low-effort fix changes earnings |
| 2 guest lifetime | 7 hosting | same-origin `/api` keeps the cookie lifetime in Safari |

## Recommended order

1. **Now (closed beta blockers):** 7 hosting (unblocks stream D), 1 age (beta cohort rule), 4 low-effort fix, 2 guest lifetime, 3 art (longest lead time).
2. **Before stream B ships profiles and privacy:** 6, 8 (draft notice for beta players).
3. **After 2 weeks of beta telemetry:** 5 final prices and earn rates, 4 final gameplay values and percentile flags.
4. **Before public launch:** 1 final countries and teen policy, 8 final legal text, 7 on-call model for launch.

## Sign-off record

Product sign-off of all eight recommendations: 2026-11-07. Items marked "pending" still need the named
reviewer: the counsel-approved country list (decision 1) and the draft privacy notice (decision 8) before the
closed beta; the rest before public launch.

Fill one row per decision. A decision without a signed row is still open.

| # | Option chosen | Changes to the recommendation | Signed by | Date |
|---|---|---|---|---|
| 1 | A — adults only (18+ or local age of majority), neutral age gate before guest creation; teen access later as a separate project | gate asks birth month and year (a year alone admits some 17-year-olds); runs before any Supabase sign-up | product owner (Sergiu Molnar); counsel and DPO: country list and gate wording pending | 2026-11-07 |
| 2 | C — sliding 30 days of inactivity, absolute cap 90 days; no support transfer of guest progress | none | product owner (Sergiu Molnar); engineering lead | 2026-11-07 |
| 3 | A — contractor or studio under work-for-hire, one in-house pipeline owner; licensed packs only for neutral props; no AI-generated items | none (proposed style brief accepted as the starting brief) | product owner (Sergiu Molnar); art lead and counsel (contract) pending | 2026-11-07 |
| 4 | Keep v1 gameplay values and deadlines; low-effort fix D (full-length run for reward eligibility and score ≥ 500 for base coins); build `review_flags` with the starting thresholds | none | product owner (Sergiu Molnar); engineering lead | 2026-11-07 |
| 5 | A + C — keep earn formulas; reprice and grow the catalog (common 300–800, rare 1,500–3,000, epic 6,000–10,000; ≥ 30 items at beta); low-effort fix of decision 4; review after 2 weeks of beta data | none | product owner (Sergiu Molnar) | 2026-11-07 |
| 6 | B — signed-in players (guest or registered) only by default; "private" toggle in `/settings`; `noindex` and `no-store` on profile responses | none | product owner (Sergiu Molnar); counsel pending | 2026-11-07 |
| 7 | A — Supabase Pro + one always-on API container in the same region, same-origin `/api` behind a CDN; beta ceiling USD 150/month; on-call model 2 (named primary and backup, business hours) | container host: Fly.io (chosen 2026-11-07); region `fra` with Supabase `eu-central-1` (2026-11-10); **Supabase starts on the Free plan** (2026-11-10, product owner) and moves to Pro before the first real beta players, at about 400 MB of data, or at the first pause | product owner (Sergiu Molnar); engineering lead | 2026-11-07 |
| 8 | Ledger A (pseudonymized ledger kept without a time limit, if counsel accepts; else B); retention table as the draft; support A (email and web form, 3 business days); conservative wording | none | product owner (Sergiu Molnar); counsel and DPO: final wording pending | 2026-11-07 |
