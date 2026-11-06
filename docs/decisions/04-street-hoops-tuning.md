# Decision 4: Street Hoops tuning, deadlines and anti-automation thresholds

- Spec: [§7 Street Hoops ruleset v1](../spec/VYBE_Technical_Specification_v1.3.md#7-street-hoops-game-ruleset-v1), [§12 Observability](../spec/VYBE_Technical_Specification_v1.3.md#12-observability-and-analytics), [§17 item 4](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off)
- Evidence: `packages/game-rules/fixtures/rulesets/street-hoops-v1.json`, `services/api/src/Vybe.Api/Features/Rewards.cs`, `Features/RewardService.cs`, `Features/GameplayEndpoints.cs:31`, `Features/QuestProgress.cs:18`
- Signs off: product owner (game feel) and engineering lead (anti-automation, deadlines)
- Needed before: closed beta for the beta values; public launch for the final values (after beta telemetry)

## Question

1. Which `config` values of `street-hoops-v1` does VYBE keep for the beta, and which does it change now?
2. Are the session start deadline (10 min), the quest claim window (7 days) and the report rate limit (10 per day) correct?
3. Which anti-automation thresholds set `game_sessions.review_flags`?

## Why it matters

- **The values are integrity rules, not only feel.** The server and the client simulate the same `config` (C# and TS, golden fixtures). The `checksum` (RFC 8785 + SHA-256) is pinned in the fixture and checked at API start-up (ADR 0003 #6).
- **Rulesets are immutable after activation** (spec §4 `game_rulesets`; database trigger, ADR 0002 #6). After a production database activates `street-hoops-v1`, any change needs a new ruleset id (for example `street-hoops-v2`). Old challenges keep their ruleset.
- **The values decide the rewards.** The coin and XP tiers use the score (spec §7). [Decision 5](05-economy.md) uses the numbers below.
- **Finding: a 3-second run earns rewards.** A run is reward-eligible with 5 shots of any result (`minShotsForRewards = 5`, `RewardService.cs:38`). Check 9 of §7 only stops a submit that claims more game time than has passed. Five shots at `tMs` 0, 650, …, 2600 can be submitted 600 ms after start. Each such run pays 20 coins and 10 XP (score 0) and counts for the `play_3` quest. 20 such runs take about 2 minutes and pay **350 V-Coins per day: the same as an honest casual player** (model in [decision 5](05-economy.md)).
- **`review_flags` exists but nothing writes it.** The column is in the schema (`Vybe.Data/Entities/Gameplay.cs:41`); no code computes flags yet.

## Facts derived from the v1 values

| Fact | Value | Derivation |
|---|---|---|
| Maximum shots per run | 93 | `floor((durationMs − 1) / minGapMs) + 1` with 650 ms gaps |
| Score of N consecutive hits (no perfects) | 5 → 650; 10 → 1,650; 20 → 3,650; 30 → 5,650; 40 → 7,650 | `hitPoints` + streak bonus `min(100, 25 × (streak − 2))` |
| Maximum score | 22,900 | 93 consecutive perfects |
| Score at which coins stop growing (150) | 6,500 (about 40 consecutive hits) | `min(150, 20 + floor(score/500) × 10)` |
| Score at which XP stops growing (80) | 5,600 (about 30 consecutive hits) | `min(80, 10 + floor(score/400) × 5)` |
| `score_1500` quest | about 10 consecutive hits, or about 15 scattered hits | §8 |
| Hit window | aim ±160 of 2,000 units (16 % of the court width); power ±120 of 1,000 | `hitDx`, `hitDp` |
| Fastest basket movement | 1,400 aim units in one 4,000 ms segment (0.35 units/ms: the target moves out of a centred aim in about 460 ms) | `xMax − xMin`, `segmentMs` |

## Values to decide

"Keep" means: keep for the beta and confirm with the telemetry in the last column. Telemetry names are the §12 product events and metrics; "traces" means `game_input_traces` (kept 30 days, §11).

| Key or setting | v1 value | What it controls | Proposal for beta | Validate with |
|---|---|---|---|---|
| `durationMs` | 60000 | Run length; time cost of quests and caps | Keep | `game_started` → `game_verified` completion rate; drop-off per second (client) |
| `graceMs` | 5000 | Upload time after the run | Keep; raise to 10000 if p99 upload time on mobile > 4 s | `submitted_at − (started_at + durationMs)` distribution; `expired` rate on submit |
| `earlyToleranceMs` | 2000 | Clock tolerance for `submitted_too_early` | Keep | Rate of `submitted_too_early` from the real client must be near 0 (invalid score rate metric) |
| `segmentMs`, `xMin`/`xMax`, `pMin`/`pMax` | 4000; ±700; 300–900 | Basket speed and range (difficulty) | Keep; playtest | Per-shot `dx`/`dp` from replayed traces |
| `hitDx`/`hitDp`, `perfectDx`/`perfectDp` | 160/120, 50/50 | Difficulty | Keep; target a first-run median score of 1,000–2,500 (so `score_1500` is reachable on day 1) | Score distribution of the first 3 runs per player (`game_verified`) |
| `hitPoints`, `perfectPoints`, `streak*` | 100, 150, 3/25/100 | Score scale, reward tiers | Keep | Score percentiles p50/p90/p99 against the tier caps 5,600 and 6,500 |
| `minGapMs`, `maxEvents` | 650, 93 | Cadence; spam limit | Keep | Inter-shot gap distribution; `cadence_violation` rate |
| `minShotsForRewards` | 5 | Reward, quest and challenge eligibility | **Change** (see options) | Share of reward-eligible runs with score 0 or run time < 10 s |
| `streamId` | 54 | PRNG stream | Keep (no gameplay effect) | — |
| Session `start_deadline` | 10 min (`GameplayEndpoints.cs:31`) | Time from create to start (asset load, countdown) | Keep | p99 of create → start; `session_expired` rate on start |
| Quest claim window | 7 days (`QuestProgress.cs:18`) | How long a completed quest is claimable | Keep | Share of quests claimed on day 2–7 |
| Report rate limit | 10 per day per reporter | Report spam | Keep (stream B builds it) | Reports per reporter per day; share `dismissed` |

## Options for the low-effort run

| | Option | Effect | Effort (rough) |
|---|---|---|---|
| **A** | Keep v1 | A 3-second, 5-miss run pays like a real run. Bots and low-effort players earn the casual amount in 2 minutes. | None |
| **B** | **Rewards need a full-length run**: add `minRunMsForRewards` (for example `durationMs − earlyToleranceMs` = 58000). The API checks `submittedAt − startedAt ≥ minRunMsForRewards` for reward eligibility only; a shorter run stays `verified` with 0 rewards (like a run below 5 shots). | Each reward-eligible run costs 60 s of real time. 20 runs cost 20 minutes. | 1–2 days: new config key (simulator types, contract `StreetHoopsConfig`, consistency check), new fixtures and checksum, API check, tests |
| **C** | **Score floor for coins**: no base coins when `score < 500` (formula change in `Rewards.BaseCoins`). | Misses pay nothing; quests still count. | 0.5 day (code, tests, spec §7) |
| **D** | B and C together | A run must be full-length and score at least 500 to pay coins. | 2 days |

## Anti-automation thresholds (flags only, no automatic action)

Spec §7: flagged runs stay verified and rewarded; flags feed the admin review queue. Start with fixed values, then change to percentiles after 2 weeks of beta data (for example: flag the top 0.5 % of each signal among runs with at least 20 shots).

| Flag | Starting threshold | Minimum sample |
|---|---|---|
| `perfect_rate_high` | perfect shots / shots > 0.60 | ≥ 30 shots in the run |
| `gap_variance_low` | standard deviation of inter-shot gaps < 25 ms | ≥ 20 shots |
| `aim_variance_low` | standard deviation of `dx` < 10 and of `dp` < 10 | ≥ 20 shots |
| `daily_cap_streak` | `runs_rewarded ≥ 20` on 7 consecutive UTC days | per player |

## Recommendation

1. **Keep all gameplay values of v1 for the closed beta.** The team has no playtest data yet; changing feel values now is a guess.
2. **Choose option D for the low-effort run, before the beta.** It is the only gap that lets a script earn the same as a player with almost no cost. Do it before any production database activates `street-hoops-v1`; then v1 can still change in place (new checksum and fixtures) and no `v2` is necessary.
3. **Keep the three provisional deadlines** (10 min, 7 days, 10 per day).
4. **Build the flags** with the starting thresholds above, and review them with percentiles after 2 weeks.
5. **Re-tune after the beta** with replays: because the simulator is deterministic, the team can re-score the stored traces with candidate values before it publishes `street-hoops-v2`. Players adapt to new values, so confirm with a second short beta round.

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| New key `minRunMsForRewards` (option B/D) | `packages/game-rules/src/types.ts`, `services/api/src/Vybe.GameRules/Models.cs`, `packages/contracts/openapi/vybe-api.v1.yaml` (`StreetHoopsConfig`), `packages/contracts/src/consistency.check.ts` passes | API stream |
| Ruleset values and checksum; regenerate fixtures (`pnpm --filter @vybe/game-rules generate:fixtures`) | `packages/game-rules/fixtures/rulesets/street-hoops-v1.json` (before activation) or a new `street-hoops-v2.json` (after) | API stream |
| Eligibility check and score floor (option C/D) | `Features/RewardService.cs`, `Features/Rewards.cs`, `Features/ChallengeEndpoints.cs:140` (challenge eligibility uses the same rule) | API stream |
| `review_flags` computation in the verification transaction | `Features/GameplayEndpoints.cs` or a new `Features/ReviewFlags.cs` | API stream |
| Spec §7 table and rules, §17 item 4 resolved | spec (new version and changelog entry) | orchestrator |
| Telemetry for the "Validate with" column | §12 events, API logs (stream D) | stream D |
