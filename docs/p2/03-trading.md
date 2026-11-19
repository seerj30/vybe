# P2 area 3: Trading and gifting

- Spec: [§1 non-goal "user-to-user item transfers"](../spec/VYBE_Technical_Specification_v1.3.md#1-product-boundaries-and-personas), [§8 economy guardrails "no player transfers"](../spec/VYBE_Technical_Specification_v1.3.md#8-xp-quests-catalog-and-economy), [§16 P2 "trading only after economic abuse controls"](../spec/VYBE_Technical_Specification_v1.3.md#16-delivery-backlog-and-release-gates)
- Evidence: `Features/RewardService.cs` (`CreditAsync`), `Features/EconomyEndpoints.cs`, `services/api/db/schema.sql` (`wallet_transactions`, `player_inventory`), `Infrastructure/PairLock.cs`, [decision 5](../decisions/05-economy.md), [terms draft A3](../legal/terms-support-draft.md)
- Signs off: product owner; counsel for the terms change
- Needed before: any transfer code

> **Not legal advice.** Every legal statement is marked "to confirm with counsel".

## Question

Can players give or exchange items, and which controls must exist first?

## Facts from the repository

| Fact | Source |
|---|---|
| Ledger `wallet_transactions` is append-only (trigger); unique `ledger_key` per reason shape; 6 reasons, none for a transfer | `schema.sql:275-290, 564` |
| Inventory: one row per player and item (no stacks); `source` in `achievement_grant`, `admin_grant`, `purchase`, `starter_grant` | `schema.sql:335-344` |
| Wallet writes lock the wallet row; a two-player write needs a stable lock order (`PairLock` exists for pairs) | `RewardService.cs`, `PairLock.cs` |
| Guests are free: 10 per IP per minute; a guest has no email | `ServiceSetup.cs:185` |
| Friends exist (requests, daily counters), blocks cancel friendships | `FriendEndpoints.cs` |
| Review flags catch bot runs only; nothing looks at links between accounts | `ReviewFlags.cs` |
| **Decision 5 economy review needs 2 weeks of beta data. The beta has no real players yet** | [decision 5](../decisions/05-economy.md), roadmap |
| Terms draft: players cannot give V-Coins or items to another player | [terms draft A3](../legal/terms-support-draft.md) |

## Options

| | Option | What it is | Player value | Risk | Cost/month | Effort (streams) | Prerequisites |
|---|---|---|---|---|---|---|---|
| **T0** | **No transfers** (today) | — | none | none | 0 | 0 | — |
| **T1** | **Gift from the shop** | Player A buys a catalog item at the normal price for friend B; B gets the item; the item is not transferable again | Medium: social, birthdays, thanks | Low: no price discovery; coins only burn | 0 | 1 | Decision 5 review; terms change |
| **T2** | **Item-for-item trade between friends** | Both sides offer owned items; both confirm; one atomic swap; no coins | Medium–high | Medium: scams, farming, RMT through items | 0 | 2 | T1 live 4 weeks; abuse dashboard; terms |
| **T3** | **Marketplace with V-Coin prices** | Listings, player-set prices, fees | High for traders | High: price discovery creates real-money value for V-Coins, RMT, wash trades, consumer and gambling-law questions | 0 + moderator time | 4+ | Not planned |

## Abuse controls

| Abuse | Control (T1) | Extra control (T2) |
|---|---|---|
| Real-money trading (sell items for cash off-platform) | Gift only new shop items; received gifts are bound to the receiver; daily cap 3 gifts, weekly cap 10 | Received-by-trade items locked 7 days; value-imbalance flag (rarity bands of decision 5) |
| Multi-account farming (many free guests feed one main) | Sender and receiver must be **linked accounts**, friends for ≥ 7 days, level ≥ 5 | Same; plus account age ≥ 14 days |
| Wash trades and circular gifts | Gift graph flag: one receiver with many low-level senders; pairs that only gift each other | Cycle flag (A→B→C→A within 7 days) |
| Scams | Gift is one-way and needs no trust | Both sides shown on one confirm screen; 10 s confirm delay; any change resets both confirmations |
| Account theft | No gifts for 72 h after a new sign-in identity or email change | Same for trades |
| Bots | Review flags exist; a flagged sender cannot gift for 7 days (proposal) | Same |

## Ledger and audit needs

| Need | T1 | T2 |
|---|---|---|
| New ledger reason | `gift_purchase` (debit of A, ledger key `gift:{giftId}`) | none (no coins move) |
| Inventory source | new `gift` source with `source_reference_id = giftId` | new `trade` source |
| New table | `gifts` (sender, receiver, item, transaction, created_at) | `trades` (both players, both item lists, state machine, confirmations) |
| Atomicity | debit A and grant B in one transaction, both rows locked in id order | swap in one transaction under `PairLock` |
| Audit and support | ledger + gift row; support can reverse only an error through admin tools | trade row history; admin reversal with reason |
| J4 deletion | gift rows stay pseudonymized like the ledger (decision 8 option A; to confirm with counsel) | same |
| Telemetry | gifts per day, distinct senders per receiver, gift graph | trades per day, value imbalance |

## Dependency on the decision 5 economy review

Gifting moves value between players. It is only safe when prices and earn rates are stable. Gates before T1 (proposal):

1. At least 2 weeks of real beta data and the decision 5 review signed.
2. Median days-to-rare and catalog ownership are inside the decision 5 targets.
3. Flagged-run rate is known and stable (bots are the source of farmed coins).
4. Economy dashboard exists (decision 5 "After sign-off", stream D).

## Legal points (all: to confirm with counsel)

- Terms change: "no transfer" becomes "gift only, no resale" — to confirm with counsel.
- Whether player-to-player exchange gives V-Coins or items a monetary value under consumer, tax or anti-money-laundering rules — to confirm with counsel.
- T3 near a paid currency ([04-monetization](04-monetization.md)) can bring gambling or payment-service questions — to confirm with counsel.

## Recommendation

**T0 now. T1 (gift from the shop between linked friends) not before the decision 5 review on real beta data.** T2 only after T1 has run 4 weeks with no unresolved abuse flags. Never T3 while paid content exists or is planned. Paid items (if any, [04](04-monetization.md)) are never giftable or tradable.
