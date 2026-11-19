# P2 area 1: Chat

- Spec: [§1 non-goal "free-text chat"](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§11 "no unrestricted chat"](../spec/VYBE_Technical_Specification_v1.3.md#11-security-and-privacy), [§16 P2 "chat only with comprehensive moderation"](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates)
- Evidence: `Features/SafetyEndpoints.cs`, `Features/AdminEndpoints.cs`, `Features/ReviewFlagEndpoints.cs`, [ADR 0005](../adr/0005-accounts-and-safety.md), [decision 1](../decisions/01-age-policy.md), [decision 8](../decisions/08-legal-wording.md), [privacy notice draft](../legal/privacy-notice-draft.md)
- Signs off: product owner and counsel
- Needed before: real-time spaces ([02](02-real-time-spaces.md)) ship

> **Not legal advice.** Every legal statement is marked "to confirm with counsel". Questions: [05-counsel-questions](05-counsel-questions.md).

## Question

How can players talk to each other, and what moderation must exist first?

## Facts from the repository

| Fact | Source |
|---|---|
| **No player-written text is visible to other players today.** Handles are server-generated (`guest_` + 10 chars); players cannot set a display name | `IdentityEndpoints.cs:115` |
| Reports: reasons `offensive_name`, `cheating`, `harassment`, `other`; `details` ≤ 500 chars; 10 per reporter per day; one open report per pair | `SafetyEndpoints.cs` |
| Blocks: private, cancel challenges and friendships under the pair lock | `SafetyEndpoints.cs` |
| Moderator tools: report queue, resolve, suspend, lift; every write has a reason and an audit row; flagged runs list | `AdminEndpoints.cs`, `ReviewFlagEndpoints.cs` |
| No text filter, no mute, no message store, no appeal endpoint | code search |
| Moderators today: the product owner only; on-call is business hours, best effort | [decision 7](../decisions/07-hosting.md), roadmap |
| Adults only (18+), self-declared gate | [decision 1](../decisions/01-age-policy.md) |

## Options

| | Option | What it is | Player value | Risk | Cost/month | Effort (streams) | Prerequisites |
|---|---|---|---|---|---|---|---|
| **A** | **No free text**: preset quick-chat (about 30 fixed phrases), emotes, stickers | Player picks an id; the client shows the localized phrase | Medium: enough to greet, cheer, invite | Low: no text to moderate; spam only | USD 0 | 0.5 (inside the spaces stream) | Spaces ([02](02-real-time-spaces.md)) |
| **B** | **Friends-only direct messages** (1:1, mutual friends, linked accounts) | Free text, stored, reportable per message | High for friends | Medium: harassment, grooming risk (lower with adults only), scams, illegal content | USD 0–50 (filter vendor optional) | 2 + 1 moderation tooling | Counsel answers; legal entity; 2 named moderators; privacy notice update |
| **C** | **Public chat in spaces** (instance chat) | Free text to everyone in the instance | Highest | High: real-time abuse; strangers; public content duties | USD 50+ (filter vendor) + moderator time | 3 + 1 | All of B, plus fast response capacity |

## Moderation needs per option

| Need | A | B | C |
|---|---|---|---|
| Text filter | none | word list (open source) on send; block links and contact data | word list + hosted classifier (new processor) |
| Rate limits | 1 quick-chat per 2 s | 20 msg/min, 300/day; new account: 20/day | slow mode 1 msg/3 s; new account read-only for 24 h |
| Reports | existing player report | report a message (keeps the message as evidence) | same, plus context of the last 20 messages |
| Mute / block | block (exists); mute = hide | block ends the thread | per-player mute, instance kick, temporary chat ban |
| Moderator queue | existing | message reports with context | priority queue; auto-hide after N reports |
| Staffing (estimate) | owner | 2 named people; weekly coverage | daily coverage, including weekends |
| Response target (proposal) | 3 business days | 24 h for reports; faster for threats | hours; removal orders: possibly 1 h (see below) |

## Legal duties in the EU (all: to confirm with counsel)

| Duty | A | B | C |
|---|---|---|---|
| DSA hosting service: notice-and-action mechanism (Art. 16), statement of reasons for restrictions (Art. 17), point of contact (Art. 11–12), terms on moderation (Art. 14) — to confirm with counsel | probably already in scope (profiles, reports) — to confirm with counsel | yes — to confirm with counsel | yes — to confirm with counsel |
| DSA "online platform" duties (complaint handling Art. 20, trusted flaggers); micro/small enterprise exemption (Art. 19) — to confirm with counsel | to confirm | private messages may not count as "dissemination to the public" — to confirm with counsel | likely in scope — to confirm with counsel |
| Terrorist content removal orders: removal within 1 hour (Regulation 2021/784) for content disseminated to the public — to confirm with counsel | no | probably no (private) — to confirm | **yes, likely** — needs a reachable contact at all hours — to confirm with counsel |
| Orders from authorities (DSA Art. 9–10), duty to report threats to life (Art. 18) — to confirm with counsel | low | yes | yes |
| ePrivacy: is a DM feature an "interpersonal communications service"; may VYBE scan messages for a filter — to confirm with counsel | no | **open** | open |
| Data retention of messages; law-enforcement data requests — to confirm with counsel | none stored | needed | needed |

## Privacy impact (decision 8)

| Item | A | B | C |
|---|---|---|---|
| New data | quick-chat ids in memory only | message text, sender, recipient, time | instance messages |
| Proposed retention (counsel to confirm) | none | 90 days; reported messages until the report is resolved + 1 year | 30 days; reported as B |
| J4 deletion | nothing | delete own messages; the other party's copy: counsel to confirm | delete own messages |
| Export | nothing | own sent and received messages | own sent messages |
| Notice and DPA | presence row only | new rows; DPA if a filter vendor | same |

## Abuse risks

| Risk | A | B | C |
|---|---|---|---|
| Harassment, hate | low (fixed phrases) | medium | high |
| Scams, off-platform contact, RMT offers | none | medium (links, contact data) | high |
| Illegal content (CSAM, terror, threats) | none | low–medium | medium |
| Spam, flooding | low | low (friends only) | high |

## Recommendation

**Option A for P2: preset quick-chat, emotes and stickers inside spaces. No free text.**

- It keeps spec §1 and §11 true and needs no new legal duties beyond the current ones (to confirm with counsel).
- Option B is the next step, only when all of these are true: counsel has answered [05](05-counsel-questions.md) §1; a legal entity exists (point of contact, liability); two named moderators accept weekly coverage; the privacy notice is updated.
- Do not plan option C. A one-person team cannot meet hour-level removal duties (to confirm with counsel).

## After sign-off

| Change | Area | Owner |
|---|---|---|
| Quick-chat phrase manifest (`assets/manifests/quickchat.v1.json`), sticker art | assets | spaces stream |
| Quick-chat message in the space protocol, rate limit | `services/api` spaces hub | spaces stream |
| Spec: §1 non-goals keep "free-text chat"; add quick-chat | spec | orchestrator |
