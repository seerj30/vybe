# P2 area 4: Monetization

- Spec: [§1 "No real-money purchases or cash-out"](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§8 "Never use V-Coins as a gambling stake"](../spec/VYBE_Technical_Specification_v1.3.md#8-xp-quests-catalog-and-economy), [§16 P2 "monetization requires separate legal/design review"](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates)
- Evidence: [decision 1](../decisions/01-age-policy.md), [decision 5](../decisions/05-economy.md), [decision 7](../decisions/07-hosting.md), [decision 8](../decisions/08-legal-wording.md), [terms draft](../legal/terms-support-draft.md), [privacy notice draft](../legal/privacy-notice-draft.md)
- Signs off: product owner, counsel, an accountant
- Needed before: any payment code

> **Not legal or tax advice.** Every legal and tax statement is marked "to confirm with counsel" (or with an accountant for tax). Costs are estimates; check provider pages.

## Question

Does VYBE take real money in P2, in which form, and what must exist before any code?

## Facts that limit the choice

| Fact | Source |
|---|---|
| The product owner is a private person. **No company, no sole-trader registration today** | [roadmap](../roadmap.md) hosting row ("no company") |
| Hosting costs USD 0; no own domain; support email not set up | roadmap, ADR 0009 |
| Supabase Free has no automatic backups (weekly encrypted dump only) | ADR 0009, `backup-restore.md` |
| V-Coins have no money value and cannot be bought; terms draft states this | spec §1, terms draft A3 |
| Adults only, self-declared month and year of birth | decision 1 |
| Art is code-built in the repository; no contractor yet | decision 3 (amended) |
| No real players yet: no data on willingness to pay | roadmap |

## Options

| | Option | What it is | Player value | Risk | Cost/month (estimate) | Effort (streams) | Prerequisites |
|---|---|---|---|---|---|---|---|
| **M0** | **No monetization** | Status quo | — | none | 0 | 0 | — |
| **M1** | **Supporter pack** | One-time payment in EUR (for example EUR 5); badge + 3 exclusive cosmetics; no currency | Low–medium: support the game | Low | provider fee per sale (MoR about 5 % + EUR 0.50) + accountant | 2 | All prerequisites below |
| **M2** | **Cosmetic store with direct EUR prices** | Single items priced in EUR; no paid currency | Medium | Medium: consumer-law duties per item | as M1 | 2.5 | As M1; price display rules |
| **M3** | **Paid currency** (buy "gems", spend on items) | Second currency next to V-Coins | Medium | High: EU consumer authorities' key principles on in-game virtual currencies (2024) — to confirm with counsel; real-money value pressure on trading ([03](03-trading.md)) | as M1 | 3 | As M2; currency rules review |
| **M4** | **Season pass** | Paid track of rewards per 6–8 weeks | Medium–high | Medium: needs a steady flow of new art (code-built today); time pressure | as M1 + content work | 3 + content | As M2; art capacity |
| **M5** | **Ads** | Display or rewarded ads | Low (players dislike) | Medium: tracking consent, new processors | revenue near 0 at small scale (estimate) | 1.5 | Consent platform; DPA with the ad network; privacy notice |

## Legal and tax prerequisites in the EU and Romania (all: to confirm with counsel or an accountant)

| Topic | What to clarify | Applies to |
|---|---|---|
| Legal form | Sale of digital content as a business needs a registered form: for example PFA (authorized natural person) or SRL (limited company, micro-enterprise tax regime) — to confirm with counsel and an accountant | M1–M5 |
| VAT | Electronic services to EU consumers: VAT of the customer's country above the EU-wide EUR 10,000 cross-border threshold, declared through the One-Stop Shop (OSS); below it, Romanian VAT rules — to confirm with an accountant | M1–M4 |
| Invoicing | Romanian e-invoicing (RO e-Factura) rules for B2C sales — to confirm with an accountant. A merchant of record (MoR) sells to the player in its own name and issues the invoice — to confirm with counsel | M1–M4 |
| Withdrawal right | 14-day withdrawal for distance contracts; for digital content it can end when delivery starts with the consumer's prior express consent and acknowledgment, confirmed on a durable medium — to confirm with counsel | M1–M4 |
| Withdrawal function | An online "withdraw from contract" function for distance contracts from 2026 (Directive (EU) 2023/2673) — to confirm with counsel | M1–M4 |
| Conformity | Digital Content Directive: the item must work as described for the stated time; end-of-service wording — to confirm with counsel | M1–M4 |
| Fair practices | Unfair Commercial Practices Directive: no pressure, clear real-money prices, no hidden currency conversion — to confirm with counsel | all |
| Random paid rewards | **Never.** No paid loot boxes, no randomized paid items (gambling-law risk) — to confirm with counsel per country | all |
| Adults only | Payments come from adults by policy; the gate is self-declared. Counsel: is the gate enough for paid content, and what does VYBE do with a minor's chargeback — to confirm with counsel | all |
| Privacy | Payment provider as processor or independent controller; payment data never stored by VYBE; notice update (decision 8) — to confirm with counsel | all |

## Payment provider options

| Provider type | Examples | Who handles VAT and invoices | Fee (estimate) | Individual without company? |
|---|---|---|---|---|
| **Merchant of record** | Paddle, Lemon Squeezy, FastSpring, Xsolla (games) | the MoR | about 5 % + EUR 0.50 | some accept individuals — to confirm per provider |
| Payment processor | Stripe, Adyen, PayPal | VYBE (OSS, invoices) | about 1.5–3 % + EUR 0.25 | needs a registered business in practice — to confirm |

## Refunds and chargebacks

| Event | Design (proposal) |
|---|---|
| Refund (withdrawal or goodwill) | Provider webhook → revoke the entitlement; items leave the inventory; audit row |
| Chargeback | Same as refund; block new purchases after 2 chargebacks; provider dispute fee (about EUR 15–20, estimate) |
| Data | New tables `payments` and `entitlements` (provider ids only, no card data); idempotent webhook by provider event id; never through the V-Coin ledger |
| Paid items | Not giftable, not tradable; never purchasable with V-Coins and V-Coins never purchasable with money |

## Store and price rules (proposal)

1. Prices in EUR, VAT included, shown before the checkout.
2. Cosmetic only; no gameplay advantage (spec §8).
3. No paid currency, no random rewards, no countdown timers on paid items.
4. Optional monthly spend cap per player (for example EUR 50) as fraud control.

## Recommendation

**M0 for P2. When the prerequisites exist, start with M1 (one-time supporter pack) through a merchant of record.** Do not build M3, M4 or M5.

Prerequisites before any payment code, in order:

1. Real players for at least 4 weeks and a signal that players want to support the game.
2. Legal form chosen with an accountant (PFA or SRL) and registered.
3. Counsel review: terms, withdrawal flow, privacy notice, country list ([05](05-counsel-questions.md) §4).
4. Own domain and a support email (also needed for real players, roadmap).
5. Supabase Pro (backups) — already planned before real beta players (decision 7).
6. MoR account approved for the legal form.
7. Spec change: §1 "no real-money purchases" becomes "no real-money currency"; a new decision file signs it.
