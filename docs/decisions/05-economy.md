# Decision 5: Economy balance (earn, spend, daily caps, prices)

- Spec: [§7 rewards and caps](../spec/VYBE_Technical_Specification_v1.3.md#7-street-hoops-game-ruleset-v1), [§8 XP, quests, catalog and economy](../spec/VYBE_Technical_Specification_v1.3.md#8-xp-quests-catalog-and-economy), [§12](../spec/VYBE_Technical_Specification_v1.3.md#12-observability-and-analytics), [§17 item 5](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off)
- Evidence: `services/api/src/Vybe.Api/Features/Rewards.cs`, `Jobs/ChallengeResolvedJob.cs:17-18`, `assets/manifests/quests.v1.json`, `assets/manifests/catalog.v1.json`
- Signs off: product owner; engineering lead reviews the caps
- Needed before: closed beta (beta values); final values after 2 weeks of beta telemetry

## Question

How many V-Coins does a player earn per day, and what do items cost, so that players have goals for weeks and not for days?

## Why it matters

- V-Coins have no monetary value and cannot move between players (spec §1, §8). So the risk is not fraud or real-money inflation. **The risk is content exhaustion**: when a player owns everything, the shop loses its pull.
- The earn rules are code constants and tests (`Rewards.cs`, `ChallengeResolvedJob.cs`). The quest rewards and all prices are content (`quests.v1.json`, `catalog.v1.json`). **A price change is cheaper than an earn change.**
- Spec §8 asks for "daily mint/burn and average days-to-item". This file gives the first model.

## Current rules

| Source | Rule | Daily limit |
|---|---|---|
| Run coins | `min(150, 20 + floor(score/500) × 10)`; run 1–10 full, run 11–20 quarter, run 21+ zero | 1,500 + 370 = 1,870 |
| Run XP | `min(80, 10 + floor(score/400) × 5)` | 800 gameplay XP |
| Quests | 3 daily quests, 100 coins + 60 XP each | 300 coins, 180 XP |
| Challenge win | +50 coins | 5 wins = 250 |
| Level-up | +100 coins per new level; level `L` at `100 × (L−1)²` XP | no daily limit; 4,900 in total (levels 2–50) |
| **Maximum per day** | | **2,420 coins + level rewards** |

Current catalog (priced items only; 3 starter items are free): Sweatband 100, Swish emote 200, Court Jersey 300, High Top 350, Neon Hoodie 400 (level 3), Gold Chain 450 (level 2), Gold Highs 650 (level 5). **Total 2,450.**

## Worked model

Personas are assumptions. Real score and run counts come from the beta.

| Persona | Runs/day | Score per run | Quests/day | Challenge wins/day | Coins/day (without level rewards) | XP/day |
|---|---|---|---|---|---|---|
| Low-effort (5 misses, about 3 s per run, see [decision 4](04-street-hoops-tuning.md)) | 20 | 0 | 1 (`play_3`) | 0 | 250 + 100 = **350** | 260 |
| Casual | 3 | 1,500 | 2 | 0 | 150 + 200 = **350** | 195 |
| Regular | 10 | 3,000 | 3 | 1 | 800 + 300 + 50 = **1,150** | 630 |
| Capped | 20 | ≥ 6,500 | 3 | 5 | 1,870 + 300 + 250 = **2,420** | 980 |

Simulation over 30 days, with level rewards, buying the cheapest affordable item first:

| Persona | Level / coins earned after day 1 | after day 7 | after day 30 | Day when the whole current catalog is owned |
|---|---|---|---|---|
| Casual | 2 / 450 | 4 / 2,750 | 8 / 11,200 | day 9 |
| Regular | 3 / 1,350 | 7 / 8,650 | 14 / 35,800 | day 3 |
| Capped | 4 / 2,720 | 9 / 17,740 | 18 / 74,300 | day 2 (Gold Highs needs level 5) |

Days of earnings per item, for the casual persona (350 per day): Sweatband 0.3, Court Jersey 0.9, Gold Highs 1.9. Even the spec §8 top range (rare outfits up to 1,500) is about 4 casual days.

**Result: earnings are about 4 times too high for the current catalog.** A casual player earns 11,200 V-Coins in 30 days against a catalog of 2,450.

## Options

| | Option | Effect | Effort (rough) |
|---|---|---|---|
| **A** | **Keep earn rules; grow and reprice the catalog.** Price bands: common 300–800, rare 1,500–3,000, epic 6,000–10,000. At least 30 priced items at beta. | Casual: first item on day 1, a common every 1–2 days, a rare in about 1 week, an epic in 3–4 weeks. Regular: an epic in about 1 week. | Content only (`catalog.v1.json` new version). Item count depends on art ([decision 3](03-art-direction.md)). |
| **B** | **Cut earn rules; keep the catalog.** For example quests 50 coins, full coins for runs 1–5, quarter coins for runs 6–20. | Casual about 250/day (3 runs unchanged, quests halved): whole catalog in about 8 days. Regular about 700/day. The cut hits regular players most and casual players least, so it does not fix the casual case. | 1 day: constants, quest manifest, tests, spec §7–§8. |
| **C** | **Fix the low-effort gap** ([decision 4](04-street-hoops-tuning.md) option D) | Low-effort income drops from 350 to 100 per day (quest only); honest players are unchanged. | 2 days |
| **D** | **Time-limited items** (`available_from`/`available_until` already in the schema) | Gives return reasons without higher prices. Can feel like pressure; avoid if minors can play ([decision 1](01-age-policy.md)). | Content only |

## Recommendation

**A + C for the closed beta. No change to the earn formulas yet.**

Reasons:

- The earn rules are already capped, tested and shared with quests and challenges. A price change is content-only and can change again without code.
- Fixing the low-effort gap (C) keeps the earn rate meaningful: coins reward play, not taps.
- Target values for the beta (to confirm with telemetry): median days-to-first-purchase ≤ 1; casual days-to-rare 5–10; share of players who own the whole catalog after 30 days < 10 %.
- Do not use option D in MVP. Time pressure is a risk for younger players and it is not needed to test the core loop.

## Telemetry that validates the model

| Question | Source |
|---|---|
| Daily mint per reason (`game_reward`, `quest_reward`, `level_reward`, `challenge_reward`) and daily burn (`purchase`) | `wallet_transactions` (exact; no client event needed) |
| Runs and score per player per day (persona mix) | `game_verified` event, `player_daily_counters` |
| Share of players at the run cap (20) and at the XP cap (800) | `player_daily_counters` |
| Days from `guest_started` to first `purchase_completed`; days-to-item per rarity | §12 events `guest_started`, `purchase_completed` |
| Shop interest without purchase | `shop_viewed` without `purchase_completed` |
| Median and p90 wallet balance (unspent coins mean too few items or too high prices) | `wallets.balance` |
| Quest claims per quest | `wallet_transactions` reason `quest_reward` (§12 has no `quest_claimed` event; add one if the client needs it) |

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| New catalog version with more items and the price bands | `assets/manifests/catalog.v2.json` (or v1 before production seeding), `catalog_version` | product owner, art owner |
| Spec §8 initial catalog ranges and economy targets | spec (new version and changelog entry) | orchestrator |
| Low-effort fix | see [decision 4](04-street-hoops-tuning.md) | API stream |
| Economy dashboard (mint/burn, balances, days-to-item) | admin or analytics (stream D), spec §12 | stream D |
| Economy review 2 weeks after beta start; then earn changes, if any, in `Rewards.cs`, `ChallengeResolvedJob.cs`, `quests.v1.json` | — | product owner |
