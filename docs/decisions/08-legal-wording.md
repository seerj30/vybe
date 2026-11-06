# Decision 8: Support, retention and deletion legal wording

- Spec: [§2 J4](../spec/VYBE_Technical_Specification_v1.3.md#j4-deletion), [§8 purchases](../spec/VYBE_Technical_Specification_v1.3.md#8-xp-quests-catalog-and-economy), [§11 GDPR and retention](../spec/VYBE_Technical_Specification_v1.3.md#11-security-and-privacy), [§12 consent](../spec/VYBE_Technical_Specification_v1.3.md#12-observability-and-analytics), [§16 social beta gate](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates), [§17 item 8](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off); [ADR 0002 #6, #11](../adr/0002-database-schema.md)
- Signs off: legal counsel, the data protection officer (if the company has one) and the product owner; the support lead accepts the support process
- Needed before: closed beta (a privacy notice and terms for beta players); final text before public launch

> **This file is not legal advice and contains no legal text.** It lists what the design does, so
> that counsel can write accurate wording. Every retention period and lawful basis below is a
> proposal for counsel to confirm or change, per jurisdiction.

## Question

1. Which retention period applies to each data category, and on which lawful basis?
2. What do the privacy notice, the terms and the in-app texts say about deletion, export, V-Coins and purchases?
3. What does support do, through which channel, and with which limits?

## Why it matters

- **Launch gate.** Spec §16 needs a "documented privacy policy" for the social beta. Spec §11: "final policy requires legal/privacy approval".
- **The wording must match the code.** The design does not delete everything. ADR 0002 #11: deletion is anonymization, because the ledger is append-only and references the player with RESTRICT. A notice that promises "we delete all your data" would be false.
- **Some data has no retention path yet.** The ledger has no retention path at all (ADR 0002 #6). Expired guest players are never removed ([decision 2](02-guest-sessions.md)). `deletion_requests` rows outlive the player ("Retained per §11 retention policy", §4).
- **Processors.** Supabase (database and Auth), the container host, the CDN and Sentry process personal data ([decision 7](07-hosting.md)). Each needs a data processing agreement and a place in the notice.

## Data inventory and proposed retention

"Design support" says what the code does today or what the spec requires.

| Data | Where | Design support | Proposed retention (counsel to confirm) | Lawful basis to confirm |
|---|---|---|---|---|
| Player profile: handle, display name, avatar | `players`, `avatar_configs` | J4 anonymization clears handle (→ `d_` + 18 random chars), display name, auth subject, avatar | Until deletion request; guests: 30 days after the last guest session expired ([decision 2](02-guest-sessions.md)) | contract (service) |
| Email and login identity | Supabase Auth (not in `vybe`) | Not yet: J4 must also delete the Supabase Auth user | Until deletion request | contract |
| Guest session tokens (hash only) | `guest_sessions` | Revoked on link and deletion | Delete 30 days after expiry or revocation | contract; security |
| Wallet ledger (amounts, reasons, item ids, pseudonymous player id) | `wallet_transactions` | Append-only, no delete path; after J4 linked only to the anonymized player id | **Decision needed** (see options) | legitimate interest (fraud, economy integrity) or legal obligation, to confirm |
| Inventory, saved outfits, quests, daily counters | `player_inventory`, `saved_outfits`, `player_quests`, `player_daily_counters` | Workflow in J4 (stream B) | Delete on J4 (counters: 90 days) | contract |
| Game sessions (score, rewards, review flags) | `game_sessions` | Immutable after verification (ADR 0002 #6) | Keep pseudonymized with the ledger, or delete on J4 | as ledger |
| Raw input traces | `game_input_traces` | Index on `received_at` for retention (ADR 0002) | 30 days (§11) | legitimate interest (anti-cheat) |
| Challenges | `challenges` | Reference both players | Keep pseudonymized (the other participant's history needs it) | legitimate interest |
| Blocks | `player_blocks` | — | Delete on J4 of either player | legitimate interest (safety) |
| Reports | `player_reports` (`details` free text) | J4 clears the free text (ADR 0002 #11) | Resolved reports: 1 year, then delete or anonymize | legitimate interest (safety, appeals) |
| Admin audit log | `admin_audit_logs` | Append-only; delete only after 90 days through `vybe.retention_delete` (ADR 0002 #6, #11) | 90 days (§11 anti-fraud audit) — counsel may require longer for moderation decisions | legitimate interest; legal claims |
| Export files | `data_export_requests.storage_key` | Short-lived download URL (§5) | File deleted at `download_expires_at`; row 90 days | legal obligation (access right) |
| Deletion requests | `deletion_requests` (no FK) | Status by capability id, no personal data in the response (§5) | 3 years as proof of erasure — counsel to set | legal obligation (accountability) |
| Idempotency records | `idempotency_records` | `expires_at` = created + 72 h; cleanup job is stream D | 72 hours | contract |
| Operational logs and traces | log platform, OpenTelemetry | §12: pseudonymous player id, no raw tokens or PII | 30 days (§11) | legitimate interest (security, operations) |
| Client crash reports | Sentry | §12 | 30–90 days (vendor setting) | legitimate interest; consent if counsel requires it |
| Product analytics events | §12 event list | Consent required (§12); not built | 13 months or less | consent |
| Backups | database host | Spec J4: "never promise immediate total erasure of backups" | Plan retention (for example 7 days) or PITR window; deleted data expires with the backup | as source data |

## What the design already supports

| Right or duty | Design |
|---|---|
| Access and portability | `POST /me/data-export` → short-lived single-player download (§5, `data_export_requests`) |
| Erasure | `POST /me/deletion-requests` with reauthentication for registered players; anonymization; status by capability id (§5, J4, ADR 0002 #11) |
| Accountability | `admin_audit_logs` with reason for every admin write; append-only (§5 Admin, ADR 0002 #6) |
| Consent points | Analytics events "consent/privacy compliant" (§12); age gate ([decision 1](01-age-policy.md)) |
| Data minimization | Guests give no email; only hashes of tokens; logs without PII (§11, §12) |
| Appeals | §11 requires an "appeal channel"; no endpoint yet |
| Purchases | "All purchases final in MVP except support correction for errors; clearly communicate terms before purchase" (§8); admin adjustment with audit (§5) |

## Options for the ledger after deletion

| | Option | Effect | Effort |
|---|---|---|---|
| **A** | **Keep the pseudonymized ledger without a time limit** (current design) | Economy audit and fraud review stay complete. Counsel must accept that a pseudonymous id with no other link is sufficient. | None |
| **B** | **Keep it for a fixed period (for example 2 years after deletion), then delete the player's rows** | Clear end date in the notice. Needs a retention path through the append-only trigger (like `vybe.retention_delete`), and a change to the RESTRICT references. | 3–5 days: migration, guards, job, tests |
| **C** | **Delete everything on request** | Simplest wording. Not possible with the current design (append-only ledger, RESTRICT FKs, challenge history of the other player). | Large redesign |

## Wording that counsel must provide

| Text | Where it appears | Facts from the design that the text must match |
|---|---|---|
| Privacy notice | landing page, `/settings`, account linking | inventory above; processors; region ([decision 7](07-hosting.md)); age rule ([decision 1](01-age-policy.md)) |
| Terms of service | landing page, guest start | V-Coins have no monetary value, no cash-out, no transfer, can be adjusted by support for errors; no gambling (§1, §8) |
| Guest notice | `/play`, hub | guest progress is lost when the guest session ends or the cookie is lost; link to keep it ([decision 2](02-guest-sessions.md)) |
| Purchase confirmation | `/shop` | purchases are final; correction only for errors (§8) |
| Deletion confirmation and status | `/settings`, deletion status page | what is anonymized, what is kept and how long, backup window; the action cannot be undone (J4) |
| Export description | `/settings` | contents and download expiry (§5) |
| Cookie notice | first visit | `__Host-vybe_guest` (authentication); analytics only with consent |
| Moderation and appeals | report flow, suspension message | how to appeal a suspension (§11) |

## Support process options

| | Option | Effect | Effort |
|---|---|---|---|
| **A** | **Email inbox with a web form, response target 3 business days**, admin tools of §5 for corrections | Enough for a closed beta. All corrections are audited. | Low |
| **B** | Help-desk tool with ticket ids and templates | Better tracking at scale; one more processor | Medium; subscription |

Support limits for every option: no transfer of guest progress ([decision 2](02-guest-sessions.md)); no account merge (J3); wallet corrections only through `POST /admin/wallet-adjustments` with a reason; identity checks before any data-rights request that does not come from the signed-in player.

## Recommendation

1. **Ledger: option A, if counsel accepts the lawful basis; otherwise option B.** A needs no code and keeps the economy audit complete.
2. **Use the retention table above as the draft for counsel.** Add the two missing paths: guest cleanup ([decision 2](02-guest-sessions.md)) and Supabase Auth user deletion inside J4.
3. **Support: option A for the beta**, with the support limits above stated in the terms.
4. **Keep the wording conservative:** say "anonymize" where the design anonymizes; state the backup window; never promise immediate total erasure (J4).

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Privacy notice, terms, cookie notice, in-app texts | `apps/web` (pages and copy), versioned so that consent records can store the version | counsel, stream C successor |
| Retention jobs: traces 30 days, audit log 90 days, idempotency 72 h, guest cleanup, export files | `services/api/src/Vybe.Api/Jobs/` | stream B (privacy), stream D (cleanup) |
| J4: also delete the Supabase Auth user; clear the fields of ADR 0002 #11 | `Features/Privacy*` | stream B |
| Ledger retention path (only if option B) | `services/api/src/Vybe.Data` migration and guards; ADR 0002 update | API stream |
| Appeal channel and support address | `apps/web`, `docs/runbooks/` | product owner, support lead |
| Data processing agreements with every processor | outside the repository | counsel |
| Spec §11 retention defaults become the approved policy; §17 item 8 resolved | spec (new version and changelog entry) | orchestrator |
