# Decision 1: Age eligibility, minors and supported countries

- Spec: [§1](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§11](../spec/VYBE_Technical_Specification_v1.3.md#11-security-and-privacy), [§16 social beta gate](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates), [§17 item 1](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off)
- Signs off: product owner and legal counsel (and the data protection officer, if the company has one)
- Needed before: closed beta (cohort rule) and public launch (full policy)

> **This file is not legal advice.** It lists the product and engineering facts. Counsel must
> confirm every legal point for every country where VYBE is available.

## Question

1. What is the minimum age to play VYBE?
2. What does VYBE do for players under 18 (or under the local age of majority)?
3. In which countries is VYBE available at launch?

## Why it matters

- **Launch gate.** Spec §16 blocks the social beta without a "documented privacy policy and age policy". Spec §1 limits the closed beta to "an appropriate invited cohort until resolved".
- **Audience risk.** VYBE has cartoon avatars, an arcade game and cosmetic items. Regulators and counsel can decide that such a service is "likely to be accessed by children", whatever the declared minimum age. Counsel must assess this.
- **No age data exists.** `vybe.players` has no age, birth date or consent field ([§4](../spec/VYBE_Technical_Specification_v1.3.md#core-tables), `services/api/src/Vybe.Data/Entities/`). `POST /guests` creates a player with no questions ([§5](../spec/VYBE_Technical_Specification_v1.3.md#identity--avatar), `Features/IdentityEndpoints.cs`). An age gate is therefore new work in the contract, the schema and the web app.
- **No country control exists.** The API does not read the client country. A country limit needs an edge rule (CDN or proxy geolocation) or a self-declared country.
- **Other decisions depend on it.** Profile defaults ([decision 6](06-profile-visibility.md)), retention and wording ([decision 8](08-legal-wording.md)) and analytics consent (§12) change when minors can play.

## What the design already supports

| Protection | Where | Status |
|---|---|---|
| No free-text chat, no player-generated content, no trading | §1 non-goals | by design |
| No real money, no cash-out, V-Coins have no monetary value, no gambling stake | §1, §8 | by design |
| Only free text: `display_name` (40 chars) and report `details` (500 chars) | §4 | by design; filtering is stream B |
| Block and report, moderation queue, audit log | §4, §5 Safety and Admin | schema done; endpoints are stream B |
| Guests give no email; email only on account linking through Supabase Auth | §2 J3 | done |
| Data export and deletion (anonymization) | §2 J4, ADR 0002 #11 | schema done; workflow is stream B |
| Product events only with consent | §12 | not built |
| Invite links do not leak to analytics (`Referrer-Policy: no-referrer`) | §11 | spec; headers are stream D |

## What counsel must check

Check each item per country. Do not assume that the answer is the same in all countries.

1. The age at which a player can give consent to data processing without a parent. In the EU, GDPR Article 8 lets each member state set this age; counsel must give the value for each launch country.
2. Whether children's privacy laws apply (for example COPPA in the United States for players under 13) and what a "general audience" service must do if it learns that a player is under age.
3. Whether a children's design code applies (for example the UK Age Appropriate Design Code), and which defaults it requires (high-privacy defaults, no nudge techniques, data minimization).
4. Platform duties for minors (for example the EU Digital Services Act) and whether VYBE is in scope.
5. Consumer law for virtual items given to minors, even without real money.
6. Whether a self-declared age gate is sufficient, or whether stronger age assurance is necessary.
7. The countries where the company can operate without a local representative, and the countries to exclude.

## Options

| | Option | Product effect | Compliance scope | Engineering effort (rough) |
|---|---|---|---|---|
| **A** | **Adults only (18+ or local age of majority).** Neutral age gate (enter birth year, no hint of the "correct" answer) before guest creation. Under-age answer: no account, no cookie, a short explanation. Small set of launch countries. | Smallest audience. Teens who want to play are turned away. | Smallest. Counsel must still assess "likely to be accessed by children". | 3–5 days: age-gate screen on `/play`, `POST /guests` field, consent record, edge country rule, tests. |
| **B** | **16+ (or the local digital consent age, between 13 and 16, per country).** Same gate. Teen players get high-privacy defaults (profile restricted, analytics off by default). | Larger audience, includes older teens. | Medium. Needs a per-country age table and a data protection impact assessment. | 6–10 days: option A plus an age band on the player, per-country rules, teen defaults in profile and analytics. |
| **C** | **13+ with parental consent where the law requires it.** | Largest legal audience. | Large. Parental consent flow, verification, parent rights to see and delete data. | 4–8 weeks, plus a vendor for parental consent or age assurance. |
| **D** | **All ages, including under 13.** | Not recommended. | Very large (children's privacy law in many countries). | Not estimated. |

## Recommendation

**Option A for the closed beta and for the first public launch. Plan option B as a separate project after counsel review.**

Reasons:

- It is the most conservative option that the current design can support in a few days.
- The design already has no chat and no real money, which lowers risk. But the design does not have teen-specific defaults or any parental flow yet.
- Option B adds per-country rules and teen defaults. Do it after the beta, when counsel has done the impact assessment and the team has telemetry.
- **Closed beta:** invite only adults whom the team knows (for example staff, friends and a recruited test panel that confirms age). Use only the countries that counsel approves.

Data minimization: store the **result** of the gate (`age_confirmed_at`, the policy version and the self-declared band), not the birth date. Counsel must confirm this.

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Replace the "Age policy decision required" text with the policy | `docs/spec/VYBE_Technical_Specification_v1.3.md` §1, §11 (new spec version and changelog entry) | orchestrator |
| Add the age-gate step before `POST /guests` in J1 and in J3 (a Supabase login that creates a player) | spec §2, §3 (`/play` states) | orchestrator |
| Add the gate result to `POST /guests` (for example `ageConfirmation`) and a new error code for an under-age answer, if the server must refuse it | `packages/contracts/openapi/vybe-api.v1.yaml`, ADR 0001 | stream B or a new stream |
| Store the consent record (new table, for example `player_consents(player_id, kind, policy_version, created_at)`) | `services/api/src/Vybe.Data` new migration, `db/schema.sql` | stream B |
| Age-gate screen, under-age screen, policy links | `apps/web` | stream C successor |
| Country rule at the edge, and the list of countries | `infra/` (stream D) | stream D |
| Privacy notice and terms text that state the age rule | [decision 8](08-legal-wording.md) | counsel |
