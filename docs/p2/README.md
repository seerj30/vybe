# P2 planning pack

This pack prepares the P2 decisions of [spec §16](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates):
"real-time spaces, chat (only with comprehensive moderation), trading (only after economic abuse controls),
monetization (requires separate legal/design review)". It uses the format of the [§17 sign-off pack](../decisions/README.md):
question, evidence, options, recommendation, changes after sign-off.

The pack changes no code, no spec text and no signed decision. **It is not legal or tax advice:** every legal
statement is marked "to confirm with counsel". Numbers marked "estimate" are not measurements.

| File | Area |
|---|---|
| [01-chat.md](01-chat.md) | Chat and moderation |
| [02-real-time-spaces.md](02-real-time-spaces.md) | Shared 3D spaces, presence, capacity |
| [03-trading.md](03-trading.md) | Gifting and trading |
| [04-monetization.md](04-monetization.md) | Real money |
| [05-counsel-questions.md](05-counsel-questions.md) | Questions for the lawyer and the accountant |

## Summary

VYBE can build **real-time spaces with no free text** now, on the free VM, with no new legal blocker except a
privacy notice row (to confirm with counsel). Free-text chat, gifting and money each need something the project
does not have yet: a legal entity, counsel answers, moderators, or real-player data.

## Decisions for the product owner

| # | Decision | Recommendation | Blocked by | Effort (streams) |
|---|---|---|---|---|
| P2-1 | Space scope | **Public plaza, instances of max. 16 avatars**; friend room visits if time allows ([02](02-real-time-spaces.md) S2) | — | 2 |
| P2-2 | Real-time architecture | **SignalR in the existing API**, binary protocol, one instance; split to a second container only if the load test shows harm to the HTTP SLO ([02](02-real-time-spaces.md) A) | WebSocket load test | (in P2-1) |
| P2-3 | Chat level | **No free text: preset quick-chat, emotes, stickers** ([01](01-chat.md) A). Friends-only DMs later; public chat not planned | — | 0.5 (in P2-1) |
| P2-4 | Trading | **None now; gift from the shop between linked friends** after the decision 5 review on real data ([03](03-trading.md) T1). No marketplace | Real beta data; decision 5 review; terms change | 1 |
| P2-5 | Monetization | **None in P2.** Later a one-time supporter pack through a merchant of record ([04](04-monetization.md) M1) | Legal form, counsel, accountant, domain, Supabase Pro | 2 (later) |
| P2-6 | Legal set-up | **Send [05](05-counsel-questions.md) to counsel and choose a legal form with an accountant** before any DM or payment work | Owner budget for counsel | — |
| P2-7 | Moderators | **Name a second moderator** before any free text | People | — |
| P2-8 | Real players | **Start the beta with real players** (domain, Supabase Pro, invited adults); P2-4 and P2-5 need their data | Domain, Supabase Pro (decision 7) | — |

## Dependencies between the areas

| Area | Depends on | Reason |
|---|---|---|
| Chat (quick-chat) | Spaces | Quick-chat lives in the space protocol |
| Chat (DMs, public) | Counsel, legal entity, moderators | DSA duties, point of contact, response times (to confirm with counsel) |
| Spaces | Load test with WebSockets | Shares 1.5 OCPU with the HTTP API |
| Trading | Decision 5 review, real data | Value moves between players only when prices are stable |
| Trading (T2+) | Monetization decision | Paid items must never be tradable; a paid currency raises real-money-trading risk |
| Monetization | Legal form, counsel, accountant | Consumer law, VAT, invoices (to confirm with counsel and an accountant) |

## Recommended order

1. **Now:** P2-6 (counsel and accountant questions out; long lead time), P2-8 (real players).
2. **First build:** spaces with quick-chat (P2-1, P2-2, P2-3): one stream for the API hub and load test, one for the web scene.
3. **After 2 weeks of real data:** decision 5 review; then gifting (P2-4).
4. **After counsel answers and a legal form exists:** supporter pack (P2-5); friends-only DMs only with two moderators.

## What blocks each area

| Area | Legal | Company | Data | Cost |
|---|---|---|---|---|
| Spaces + quick-chat | privacy notice row (to confirm with counsel) | no | no | USD 0 (estimate: VM carries 30–60× the beta peak) |
| Chat (DMs) | yes: DSA, ePrivacy, retention (to confirm with counsel) | likely (to confirm with counsel) | no | filter vendor optional; moderator time |
| Trading (gifts) | terms change (to confirm with counsel) | no | **yes**: decision 5 review | USD 0 |
| Monetization | yes: consumer law, withdrawal, VAT (to confirm with counsel) | **yes** | yes: demand signal | provider fees, accountant, counsel |

## Effort

One stream = one Claude Code session with its own branch and PRs (see [roadmap](../roadmap.md)). Estimates:
spaces + quick-chat 2.5 streams; gifting 1; supporter pack 2; friends-only DMs 3 (with moderation tools). Total for
the recommended P2: about **3.5 streams** before monetization.

## Sign-off record

Fill one row per decision. A decision without a signed row is still open.

| # | Option chosen | Changes to the recommendation | Signed by | Date |
|---|---|---|---|---|
| P2-1 | | | | |
| P2-2 | | | | |
| P2-3 | | | | |
| P2-4 | | | | |
| P2-5 | | | | |
| P2-6 | | | | |
| P2-7 | | | | |
| P2-8 | | | | |
