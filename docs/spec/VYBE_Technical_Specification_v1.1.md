# VYBE — Product, Game & Technical Specification v1.1

**Status:** implementation baseline / proposed decisions, not deployed or tested. v1.1 closes the gaps found in the v1.0 review; see §18 Changelog. Values marked *provisional* need prototype validation (§17 item 4).  
**Product:** browser-based social arcade with customizable 3D cartoon avatars.  
**MVP:** guest onboarding, avatar, Street Hoops, virtual currency, shop, wardrobe, asynchronous challenges, account linking, daily quests, public profile, minimal moderation and admin.  
**Deferred:** live multiplayer, open world, chat, player trading, paid currency, My Room, second mini-game.

## 1. Product boundaries and personas

- Target: desktop and mobile web; responsive and touch friendly.
- Core loop: Create avatar → play → earn → buy/equip → challenge friend → return.
- No real-money purchases or cash-out. V-Coins have no monetary value.
- User roles: guest, registered player, moderator, administrator, service worker.
- Players may view public profiles; inventory ownership, wallet ledger, email and abuse reports are private.
- **Age policy decision required before public launch:** determine age gate, permitted ages, parental protections and jurisdictional compliance. Closed beta should restrict access to an appropriate invited cohort until resolved.
- Non-goals: open-world movement, player-generated content, free-text chat, user-to-user item transfers.

### KPIs (hypotheses, not guarantees)
- Avatar completion >=75%; first-game completion >=65%; D1 retention >=30%; challenge-link acceptance >=25%.
- Monitor cohort and device breakdowns; don't optimize for invitation spam.

## 2. User flows and failure handling

### J1: first visit
Landing → Start as guest → create guest identity → choose avatar presets → save → tutorial → start game → submit input trace → server validates → rewards → shop → purchase → equip → prompt to link account.

- If guest creation fails: retry, no local phantom identity.
- If browser closes: restore server-backed guest session via secure cookie if still valid.
- If guest cookie is lost: explain guest progress cannot be recovered without linked account.
- If avatar save fails: retain local unsaved choices, retry; never claim success.
- If game submission times out: poll session status before retrying with same idempotency key.
- If purchase times out: refresh inventory and wallet before retrying same key.

### J2: challenge
Authenticated/guest player completes eligible run → Create challenge (server stores canonical run) → share opaque URL → invitee opens → guest/login onboarding if needed → accept → play same challenge ruleset → submit → verify → resolve → show result → rematch.

- A challenge has one designated recipient **or** an invite token redeemable once by an eligible non-creator player; MVP uses single-use public invite token. A rematch sets the designated recipient (the other participant) and also issues an invite token; only the designated recipient can redeem it.
- Creator cannot accept own challenge; accepted token binds recipient once.
- No entry fees, no transferable winnings, no wagering.
- Expiry after 24 hours if unaccepted; accepted challenges allow 24 hours from acceptance to complete.
- Ties: challenger retains title; both receive normal gameplay rewards; no winner bonus; stored as `completed` with `winner_player_id = NULL`.
- Blocked players (either direction, `player_blocks`) cannot create or accept challenges with each other; `open` and `accepted` challenges between them are cancelled in the same transaction that applies the block.

### J3: account linking
Guest → verify email/OAuth identity via Supabase Auth → backend binds existing player to verified subject in atomic operation → revoke guest credentials → keep player ID, wallet, inventory and history. If verified identity is already linked to another player, **do not auto-merge**; require explicit account selection/support flow.

### J4: deletion
Request deletion → reauthentication for registered users → revoke sessions → queue deletion workflow → delete or anonymize data according to documented retention/legal obligations → return confirmation and status. Preserve minimal fraud/audit data only with documented lawful basis and retention window; never promise immediate total erasure of backups.

## 3. Screen inventory

| Route | Screen | Main actions | Required states |
|---|---|---|---|
| `/` | Landing | Start guest, login | loading, unavailable |
| `/play` | Onboarding | Start, resume | expired guest, retry |
| `/avatar/create` | Avatar creator | preview, select, save | invalid config, asset fallback |
| `/hub` | Home | quests, wallet, play | new user, partial load |
| `/arcade` | Arcade | choose Street Hoops | disabled game, loading |
| `/arcade/street-hoops` | Gameplay | start, shoot, finish | pause, background, disconnect, WebGL loss |
| `/shop` | Catalog | filter, preview, buy | insufficient funds, owned, sold out |
| `/wardrobe` | Inventory | equip, unequip, save outfit | empty, incompatible item |
| `/challenges` | My challenges | create, open, rematch | empty, expired, cancelled |
| `/challenge/:token` | Invite | accept, play, result | invalid, redeemed, expired, blocked |
| `/profile/:handle` | Public profile | view, challenge, report | private, unavailable |
| `/settings` | Account | link, privacy, export, delete | verification required |
| `/admin` | Admin | catalog, reports, economy audit | unauthorized |

Global UI: responsive desktop sidebar/mobile bottom nav; accessible focus order; reduced-motion option; fallback 2D avatar portrait on unsupported WebGL; explicit progress indicators.

## 4. Domain model and database schema

Use PostgreSQL `uuid` IDs, `timestamptz` UTC, monetary amounts as `bigint` integer V-Coins (never float), optimistic concurrency or row locks for mutable aggregates. All tables include `created_at` where relevant; migrations are additive/expand-contract.

### Core tables

**players**: `id uuid PK`, `auth_subject text UNIQUE NULL`, `handle text NOT NULL`, `handle_normalized text UNIQUE NOT NULL`, `display_name varchar(40)`, `kind enum(guest,registered)`, `status enum(active,suspended,deleting,deleted)`, `level int DEFAULT 1`, `xp_total bigint DEFAULT 0`, `created_at`, `updated_at`, `deleted_at NULL`. Handle format `[a-z0-9_]{3,20}`; case-insensitive normalized uniqueness; reserved names list.

**guest_sessions**: `id uuid PK`, `player_id uuid FK`, `token_hash bytea UNIQUE`, `expires_at`, `revoked_at NULL`, `created_at`; random high-entropy token in Secure, HttpOnly, SameSite=Lax cookie; rotate on linking. Never store raw tokens.

**avatar_configs**: `player_id uuid PK/FK`, `body_preset_id text`, `skin_tone_id text`, `face_preset_id text`, `hair_preset_id text`, `hair_color_id text`, `equipped_json jsonb`, `revision bigint`, `updated_at`; validate each preset against versioned catalog, not arbitrary asset URLs.

**catalog_items**: `id uuid PK`, `sku text UNIQUE`, `slot enum(hair,top,bottom,shoes,accessory,emote)`, `name text`, `price bigint CHECK(price>=0)`, `min_level int`, `asset_key text`, `compatible_body_presets text[]`, `rarity enum(common,rare,epic)`, `is_active bool`, `catalog_version int`, `available_from/available_until timestamptz NULL`. No arbitrary client-supplied prices.

**player_inventory**: `player_id uuid FK`, `item_id uuid FK`, `acquired_at`, `source_transaction_id uuid NULL`, `PRIMARY KEY(player_id,item_id)` for non-stackable cosmetics. Starter items are granted at onboarding and recorded as grants.

**saved_outfits**: `id uuid PK`, `player_id uuid FK`, `name varchar(40)`, `equipped_json jsonb`, `created_at`, `updated_at`; max 3 per player in MVP; each item validated against ownership and slot compatibility.

**wallets**: `player_id uuid PK/FK`, `balance bigint NOT NULL CHECK(balance>=0)`, `revision bigint NOT NULL DEFAULT 0`, `updated_at`.

**wallet_transactions**: `id uuid PK`, `player_id uuid FK`, `delta bigint NOT NULL CHECK(delta<>0)`, `balance_after bigint NOT NULL`, `reason enum(game_reward,quest_reward,purchase,level_reward,challenge_reward,admin_adjustment)`, `reference_type text`, `reference_id uuid`, `idempotency_key text NOT NULL`, `created_at`; `UNIQUE(player_id,idempotency_key)`; append-only permissions; indexes `(player_id,created_at DESC)`, `(reason,reference_id)`.

**game_rulesets**: `id text PK`, `game_id text`, `version int`, `config jsonb`, `checksum text`, `active_from`, `retired_at NULL`; immutable after activation.

**game_sessions**: `id uuid PK`, `player_id uuid FK`, `game_id text`, `ruleset_id text FK`, `seed bigint NOT NULL CHECK(seed BETWEEN 0 AND 4294967295)` (uint32, safe as a JSON number), `status enum(active,submitted,verified,rejected,expired)`, `started_at`, `expires_at`, `submitted_at NULL`, `verified_at NULL`, `score int NULL`, `reward_coins bigint DEFAULT 0`, `reward_xp int DEFAULT 0`, `input_trace_hash text NULL`, `rejection_reason text NULL`, `review_flags text[] NOT NULL DEFAULT '{}'` (anti-automation signals, §7), `challenge_id uuid NULL`, `version bigint`; unique reward reference via ledger index. Store compressed event trace separately with short retention.

**game_input_traces**: `session_id uuid PK/FK`, `compressed_trace bytea`, `encoding text`, `event_count int`, `received_at`; size cap and retention policy.

**challenges**: `id uuid PK`, `creator_player_id uuid FK`, `creator_session_id uuid FK UNIQUE` (one challenge per verified run), `recipient_player_id uuid FK NULL` (set on accept; preset for a rematch), `invite_token_hash bytea UNIQUE`, `ruleset_id text FK`, `status enum(open,accepted,completed,expired,cancelled)`, `created_at`, `accept_deadline`, `accepted_at NULL`, `complete_deadline NULL`, `recipient_session_id uuid FK UNIQUE NULL`, `winner_player_id uuid FK NULL` (NULL on a `completed` challenge = tie), `rematch_of_challenge_id uuid FK NULL`, `resolved_at NULL`, `version bigint`; `CHECK(recipient_player_id IS NULL OR recipient_player_id <> creator_player_id)`; indexes `(creator_player_id,created_at DESC)`, `(recipient_player_id,status)`, `(status,accept_deadline)`.

**quest_definitions**: `id uuid PK`, `code text`, `version int`, `period enum(daily,once)`, `criteria jsonb`, `reward_coins int`, `reward_xp int`, `is_active bool`.

**player_quests**: `player_id uuid FK`, `quest_id uuid FK`, `period_start date`, `progress int`, `status enum(active,completed,claimed)`, `claimed_at NULL`, `PRIMARY KEY(player_id,quest_id,period_start)`.

**achievements**: `id uuid PK`, `code text UNIQUE`, `criteria jsonb`, `reward_item_id uuid NULL`; **player_achievements**: `(player_id,achievement_id) PK`, `unlocked_at`.

**friendships** (P1): `requester_id`, `addressee_id`, `status enum(pending,accepted,declined)`, timestamps; canonical pair unique and no self-relations. MVP may launch with challenge links before full friendship UI. Blocking is not a friendship state; it lives in `player_blocks`.

**player_blocks** (MVP): `blocker_player_id uuid FK`, `blocked_player_id uuid FK`, `created_at`, `PRIMARY KEY(blocker_player_id,blocked_player_id)`, `CHECK(blocker_player_id <> blocked_player_id)`; index `(blocked_player_id)` for reverse lookup. A block in either direction prevents challenges between the pair. Private: the blocked player is not told.

**player_reports**: `id`, `reporter_player_id`, `target_player_id`, `reason`, `details`, `status`, `created_at`, `reviewed_at`; restricted access.

**admin_audit_logs**: `id`, `actor_subject`, `action`, `entity_type`, `entity_id`, `before_json`, `after_json`, `reason`, `created_at`; append-only and restricted.

**idempotency_records**: `player_id`, `operation`, `key`, `request_hash`, `response_status`, `response_body`, `expires_at`, unique `(player_id,operation,key)`; reject reuse with different payload.

### Database invariants
1. Every wallet debit and corresponding inventory grant commit in one DB transaction.
2. Wallet row locked (`SELECT ... FOR UPDATE`) for debits/credits; `CHECK(balance>=0)` as defense in depth.
3. Rewards use unique source reference and idempotency key, not client-supplied amount.
4. Equipping requires owned, active/usable and compatible item; only one item per exclusive slot.
5. Challenge transitions use compare-and-swap version/status predicate to avoid double acceptance.
6. Game sessions and rulesets are immutable after verification; audit corrections use compensating entries.
7. Retention and cascade policies are explicitly tested before deletion features ship.
8. Inserting a `player_blocks` row and cancelling the pair's `open`/`accepted` challenges commit in one DB transaction.
9. Challenge winner bonus uses ledger reason `challenge_reward` with `reference_id = challenge.id`; at most one per challenge.

## 5. API contracts v1

Base path `/api/v1`. JSON UTF-8; UTC ISO 8601; UUID strings; `Idempotency-Key` required for state-changing economic actions. Auth: validated Supabase bearer token for registered users or signed guest cookie for guests. `traceparent` propagated. Errors: RFC 9457 ProblemDetails with `type`, `title`, `status`, `detail`, `instance`, `code`, `traceId`. Never leak internal exception details.

### Identity / avatar
- `POST /guests` → `201 {playerId,handle,guestExpiresAt}` + secure guest cookie; rate limited.
- `POST /accounts/link` `{linkProof}` → `200 {playerId,accountLinked:true}`; proof must be verified server-side; conflicts return 409.
- `GET /me` → `200 {id,handle,level,xpTotal,walletBalance,avatarRevision}`.
- `PUT /me/avatar` `{bodyPresetId,skinToneId,facePresetId,hairPresetId,hairColorId,expectedRevision}` → `200 {revision,avatar}`; 409 on stale revision; 422 invalid presets.
- `GET /profiles/{handle}` → public fields only; 404 if unavailable.

### Safety
- `POST /me/blocks` `{handle}` → `204`; idempotent; cancels pending challenges between the pair (invariant 8); 404 if handle unknown; 422 on self-block.
- `DELETE /me/blocks/{handle}` → `204`; idempotent; does not restore cancelled challenges.
- `GET /me/blocks?cursor=&limit=` → players the caller blocked; never exposes who blocked the caller.

### Catalog / inventory / economy
- `GET /catalog/items?slot=top&cursor=...&limit=24` → `{items:[{id,sku,name,price,slot,rarity,minLevel,previewUrl}],nextCursor}`; server-generated asset URLs.
- `GET /me/inventory` → owned item IDs and metadata.
- `POST /purchases` `{itemId}` + Idempotency-Key → `200 {transactionId,itemId,balanceAfter}`; 409 `insufficient_funds` or `already_owned`, 422 `item_unavailable`.
- `POST /me/equipment` `{slot,itemId|null,expectedAvatarRevision}` → `200 {revision,equipped}`; 403 if not owned; 409 revision conflict.
- `GET /me/wallet/transactions?cursor=&limit=` → paginated read-only ledger.

### Gameplay
- `POST /games/street-hoops/sessions` `{challengeId?:uuid}` → `201 {sessionId,seed,rulesetId,serverStartAt,expiresAt,limits}`. `seed` is a JSON integer in uint32 range. The session is `active` on creation. With `challengeId`, the response carries the creator run's `seed` and `rulesetId`.
- `POST /games/sessions/{id}/submit` `{rulesetId,events:[{tMs,aimX,power}],clientReportedScore}` + Idempotency-Key. All event fields are integers in the units of §7; each event is one shot. → `202 {sessionId,status:"submitted"}` or `200 {status:"verified",score,rewards}`. Client score is diagnostic only; server recomputes.
- `GET /games/sessions/{id}` → state and verified score/rewards; no trace exposed to others.

### Challenges
- `POST /challenges` `{creatorSessionId}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`; creator session must be verified, eligible and not already used for another challenge.
- `GET /challenges/by-token/{token}` → safe public challenge preview, no secret token hash.
- `POST /challenges/by-token/{token}/accept` + Idempotency-Key → `200 {challengeId,status,completeDeadline}`; atomic token redemption; creator prohibited; if `recipient_player_id` is preset, only that player may accept (403 otherwise); 403 if a block exists in either direction.
- `GET /challenges/{id}` → participants and status, filtered by privacy rules.
- `POST /challenges/{id}/rematch` `{creatorSessionId}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`. Original challenge must be `completed`; caller must be one of its participants and becomes the new creator; the other participant becomes the preset `recipient_player_id`; `creatorSessionId` follows the same rules as `POST /challenges` (new verified run, not used before); 403 if a block exists; cannot recycle prior result.

### Quests and account
- `GET /me/quests` → active progress and claimable rewards.
- `POST /me/quests/{id}/claim` + Idempotency-Key → `200 {coinsGranted,xpGranted,level}`; 409 if incomplete/claimed.
- `POST /me/data-export` → async export request with status endpoint.
- `POST /me/deletion-requests` → 202, requires reauthentication for registered account.

### Representative DTOs
```json
// POST /purchases
{"itemId":"9cfa5aa0-73b5-4dc6-bae5-8db2c740a191"}
// 200
{"transactionId":"a7a51f20-4c91-4dc0-8a58-bc457c9bd6f2","itemId":"9cfa5aa0-73b5-4dc6-bae5-8db2c740a191","balanceAfter":250}
```
```json
// POST /games/sessions/{id}/submit
{"rulesetId":"street-hoops-v1","events":[{"tMs":820,"aimX":120,"power":780}],"clientReportedScore":100}
```

## 6. State machines

### Game session
`active -> submitted -> verified | rejected`; `active -> expired`; terminal states `verified,rejected,expired`. A session is `active` from creation; `started_at` is set at creation and is time 0 for `tMs`. There is no separate start call. Server clock is authoritative. `submitted` may be processed synchronously or asynchronously, but repeated submissions must return the canonical result. Only verified sessions may produce rewards or challenges.

### Challenge
`open -> accepted -> completed`; `open -> expired|cancelled`; `accepted -> expired|cancelled`; terminal `completed,expired,cancelled`. Acceptance binds one recipient; completion references one verified run under the challenge ruleset. `completed` with `winner_player_id = NULL` is a tie. A block in either direction moves `open|accepted -> cancelled`. A scheduled sweeper expires overdue records idempotently; read endpoints also check deadline to avoid stale acceptance.

### Quest
`active -> completed -> claimed`; period rollover creates a new record, never resets claimed history. Server derives progress from verified domain events.

### Purchase
`requested -> validated -> committed` (atomic debit + inventory grant + ledger); validation failures return no state change. Duplicate request key returns stored outcome; key reuse with different body returns 409.

### Account
`guest -> registered -> suspended? -> deleting -> deleted`; a guest token cannot be used after linking. Suspension blocks economic actions and challenge creation.

## 7. Street Hoops game ruleset v1

**Objective:** score maximum points in 60 seconds. Input can be mouse, touch or keyboard; the client quantizes it to the integer units below before it records an event.

**Input units (normative, all integers):**
- `aimX` in [-1000, 1000] (1000 = full court half-width).
- `power` in [0, 1000].
- `tMs` in [0, 59999], milliseconds since the session `started_at`.
- The trace contains **shot events only** (one event = one release). Aim/power samples between shots are not sent. Non-integer or out-of-range values => `rejected`.

**Rules (provisional values, stored in the immutable ruleset config):**
- Session duration 60,000 ms, server-authorized start with a maximum grace allowance of 5,000 ms for network upload; the grace never extends game simulation.
- Cadence: `tMs` strictly increasing; gap between consecutive events >= 650 ms; at most 93 events (0, 650, …, 59,800). Reject traces exceeding configured limits.
- **PRNG:** PCG32 (XSH-RR output, 64-bit LCG state), seeded with the reference `pcg32_srandom(initstate = seed, initseq = config.streamId)` from pcg-random.org. Draw an integer in [lo, hi] as `lo + (next() mod (hi - lo + 1))`; the small modulo bias is accepted. The JS client uses `BigInt` for the 64-bit state.
- **Basket path:** the 60 s timeline has 15 segments of `segmentMs = 4000`. Before the run, draw 16 waypoints `k = 0..15` in this order: `x_k` in [-700, 700], then `p_k` in [300, 900]. No other draws occur. For time `t`: `k = t div segmentMs`, `f = t mod segmentMs`, `targetX(t) = x_k + trunc((x_{k+1} - x_k) * f / segmentMs)`, `idealPower(t) = p_k + trunc((p_{k+1} - p_k) * f / segmentMs)`. `trunc` rounds toward zero (C# integer `/`; JS `Math.trunc`). Intermediate values stay below 2^31.
- **Shot evaluation:** `dx = |aimX - targetX(tMs)|`, `dp = |power - idealPower(tMs)|`. Perfect when `dx <= 50` and `dp <= 50`; hit when `dx <= 160` and `dp <= 120`; otherwise miss.
- **Points:** hit = 100, perfect = 150, miss = 0. `streak` = number of consecutive hits including the current shot (a perfect is a hit). Streak bonus = `min(100, 25 * (streak - 2))` when `streak >= 3`, else 0, so the bonus grows +25, +50, +75, then stays at +100. A miss resets `streak` to 0. Maximum score = 22,900 (93 consecutive perfects).
- Server replays the full trace, validates the units, monotonic timestamps, event count and cadence, then computes the canonical score. Invalid trace => `rejected`, no reward.
- **Golden fixtures:** `packages/game-rules/fixtures/*.json` holds `{rulesetId, config, seed, waypoints, events, expected:{shots:[{targetX,idealPower,outcome,points}], score}}` plus PCG32 reference vectors. The C# server and the TS client must both pass every fixture in CI.
- Client animations may use smooth Three.js physics for appearance, but the shown outcome must match the deterministic result. Prototype the feel before freezing numeric thresholds.

**Anti-automation (limit of replay validation):** the client knows the `seed` and must render the basket, so a bot can compute a perfect trace. Replay proves a trace is legal, not that a human played. MVP records signals in `game_sessions.review_flags` and takes **no automatic action**: perfect-shot rate above threshold in a run; low variance of inter-shot gaps; low variance of `dx`/`dp`; run volume at the daily cap over many consecutive days. Flagged runs stay verified and rewarded; flags feed the admin review queue. Thresholds are set from prototype and beta telemetry.

**Rewards per verified run:** `coins = min(150, 20 + floor(score/500)*10)` and `xp = min(80, 10 + floor(score/400)*5)`. First 10 verified runs/day grant full coins, next 10 grant 25% coins rounded down, remaining runs grant 0 coins; XP cap 800/day from gameplay. Daily reset UTC, explicitly shown in UI. All rewards computed on server; no reward for rejected runs. These are tuning defaults, not measured economy balance.

**Challenge rules:** creator run's immutable ruleset and seed define recipient's conditions; recipient uses separate session. Winner has higher canonical score; ties favor creator for display only and store `winner_player_id = NULL`. Winner receives +50 V-Coins (ledger reason `challenge_reward`) once per challenge, capped at 5 winner bonuses/day; standard gameplay rewards remain separate. No stake, no currency transfer between players.

## 8. XP, quests, catalog and economy

**XP thresholds:** Level 1 starts at 0; total XP required to reach level `L` (L>=1) = `100*(L-1)^2`. Level 50 at 240,100 XP. Persist total XP; derive level in domain service; no client authority. Level-up grants 100 V-Coins once per new level via ledger source `level_reward`, unique per player+level.

**Daily quests (UTC):**
1. `play_3`: finish 3 verified runs → 100 coins + 60 XP.
2. `score_1500`: achieve >=1,500 points in one verified run → 100 coins + 60 XP.
3. `challenge_1`: finish one accepted challenge → 100 coins + 60 XP.
Quests must not require inviting new people to progress; alternative solo quest should be offered for users without friends.

**Initial catalog (illustrative):** starter top/bottom/shoes free; common tops 200–400, hairstyles 250–500, shoes 350–650, accessories 100–450, rare outfits 900–1,500. No item modifies game performance. All purchases final in MVP except support correction for errors; clearly communicate terms before purchase.

**Economy guardrails:** no real money, no withdrawals, no player transfers; admin adjustments require reason and audit; cap rewards, detect abnormal patterns; analyze daily mint/burn and average days-to-item. Never use V-Coins as a gambling stake.

## 9. Avatar 3D asset contract

- Format: glTF 2.0 binary `.glb`, consistent meters/axis convention (Y-up), origin at ground center, standardized naming and versioned manifests.
- Shared humanoid rig: canonical bone hierarchy and bind pose; compatible animation retargeting. Define one authoritative rig and automated compatibility checks.
- Base presets: 2 body silhouettes; no gender-restricted clothing. All equipment declares compatible body presets.
- Slots: hair, top, bottom, shoes, accessory (max 2 accessory attachment points), emote. Top/bottom coverage masks avoid body clipping; incompatible meshes cannot be equipped.
- Each item manifest: `sku`, `slot`, `assetVersion`, `assetUrl`, `thumbnailUrl`, `rigVersion`, `bodyCompatibility`, `attachBone?`, `hideBodyRegions[]`, `textureVariants[]`, `triangles`, `textureBytes`, `downloadBytes`, `contentHash`.
- Example performance budgets to validate on target devices: avatar visible <=25k triangles at medium LOD, <=2 MB compressed initial avatar asset download excluding shared textures, textures <=1024px per major garment, <=2 skinned meshes simultaneously where possible, <=60 draw calls for avatar scene. Budgets are goals, not guarantees.
- Compression: KTX2/Basis textures, meshopt or Draco where appropriate; CDN cache immutable versioned asset paths.
- Fallback: if item fails to load, render safe starter item and show non-blocking warning; never change server-side equipped state silently.
- Visual QA: 360-degree rotation, all animations, all body presets, no clipping, lighting/skin tones, color contrast, LOD, Safari/WebGL, low-end Android.
- Do not build unique full-body meshes for every outfit combination; use modular assets and compatibility manifest.

## 10. Frontend architecture

Angular standalone feature routes, Signals for local state, RxJS for network streams; Three.js renderer hosted in an isolated `AvatarViewport` with explicit mount/dispose lifecycle. API client generated from OpenAPI; typed domain models in shared package. Lazy load 3D and gameplay bundles. Use route resolvers sparingly and skeleton UIs.

Recommended repo:
```
vybe/
  apps/web/                  # Angular UI + Three.js scenes
  services/api/              # ASP.NET Core API
  packages/contracts/        # generated OpenAPI TS clients
  packages/game-rules/       # documented fixtures, shared rule constants
  assets/                    # manifests, source asset references
  infra/                     # IaC and deployment
  tests/e2e/                 # Playwright
  docs/adr/                  # architecture decisions
```

Frontend never directly writes wallet, inventory or scores. Query cache invalidation on purchase, reward and equip. Accessibility: keyboard controls for UI, labels for interactive elements, motion-reduction mode, no color-only feedback. Handle WebGL context loss with reloadable scene and safe fallback.

## 11. Security and privacy

- Threat model: score forgery, bot farming, replayed rewards, guest token theft, IDOR, XSS, malicious asset references, invitation enumeration, admin abuse, account linking takeover.
- Auth: validate JWT issuer/audience/signature and token expiry; guest cookie with CSRF protection on cookie-authenticated mutations; secure CORS and origin checks.
- Authorization: resource owner policies on every endpoint; separate moderator/admin claims; never trust client `playerId`.
- API: per-IP and per-player rate limits; payload/event count/size caps; structured validation; request timeouts; parameterized SQL; no arbitrary uploaded 3D files in MVP.
- Invite URLs: 128-bit+ random opaque token, only hashed in DB, single-use, expiration; avoid leaking via analytics/referrers, apply `Referrer-Policy: no-referrer` on token pages.
- Browser: CSP, safe HTML escaping, dependency scans, no tokens in localStorage, secure cookies.
- GDPR: data inventory, legal bases, retention, privacy notice, export/deletion workflow, processor agreements, breach process. Determine minors policy before launch; no unrestricted chat.
- Abuse: username filtering, report/block, moderation review, audit logs, appeal channel.
- Data retention defaults for review: raw game traces 30 days, operational logs 30 days, anti-fraud audit 90 days, account data until deletion request; final policy requires legal/privacy approval.

## 12. Observability and analytics

Structured JSON logs with `traceId`, `playerId` pseudonymous identifier, `sessionId`, `challengeId`, `operation`, `result`, duration; no raw tokens or PII. OpenTelemetry traces for API and DB; Sentry for client crashes. Metrics: API error rate, p95 latency, purchase failures, duplicate idempotency attempts, invalid score rate, game completion, WebGL initialization errors, FPS samples, asset load time.

Product events (consent/privacy compliant): `guest_started`, `avatar_saved`, `tutorial_completed`, `game_started`, `game_submitted`, `game_verified`, `reward_granted`, `shop_viewed`, `purchase_completed`, `item_equipped`, `challenge_created`, `challenge_accepted`, `challenge_resolved`, `account_linked`. Each event: schema version, UTC time, pseudonymous player/session, source, outcome, ruleset version. Server emits authoritative economic events; frontend emits UI engagement events.

Alert on wallet transaction errors, score validation spikes, API 5xx, auth failures, elevated WebGL errors, backup failure. Admin dashboard for report queue, catalog toggle, player suspension and audit search; no direct unaudited balance editing.

## 13. Deployment and operations

Environments: local, preview, staging, production; isolated DB and storage per environment. Secrets in managed secret store, not frontend env. CI: formatting/lint, Angular typecheck, .NET build, unit tests, integration tests with PostgreSQL Testcontainers, SAST/dependency scan, asset manifest validation, Playwright smoke, deploy. CD: staging soak → migration compatibility → production progressive rollout → smoke tests → alert monitoring. DB migrations expand-contract, backups and quarterly restore drills; deploy rollback must not require destructive DB rollback.

Target SLOs for beta (validate): availability 99.5%; standard API read p95 <300ms excluding network/asset transfer; desktop 60 FPS and mobile 30 FPS on published target device matrix; LCP <=2.5s at p75 on non-3D landing pages. Establish actual baseline with field telemetry. Load test guest creation, game submissions, wallet transactions, and challenge acceptance; target concurrency determined by expected beta size, not invented.

## 14. Test matrix

### Unit
- PCG32 reference vectors, basket path interpolation (incl. negative deltas), streak bonus, cross-language fixture parity (C# and TS).
- XP threshold, rewards cap, score replay, combo, catalog compatibility, challenge winner/tie, quest period boundary, idempotency request-hash handling.

### Integration (real PostgreSQL)
- Concurrent purchases with insufficient funds; duplicate purchase retry; reward exactly once; account link conflict; challenge double accept; expired challenge; block cancels pending; audit trail; authorization boundaries; migrations up from clean DB.

### Browser E2E (Playwright)
- E01 guest → avatar → tutorial → verified game → wallet.
- E02 wallet → purchase → equip → reload persists.
- E03 guest → verified account linking → same player ID/inventory.
- E04 creator challenge → share link → second player accepts → verified run → result.
- E05 duplicate submit/retry → no double reward.
- E06 two simultaneous purchases → never negative balance.
- E07 expired invite and self-accept → rejected.
- E08 forbidden access to another player's wallet/inventory.
- E09 lost connectivity during purchase → consistent reconciliation.
- E10 asset load failure → fallback avatar.
- E11 mobile touch gameplay, keyboard UI navigation, reduced-motion.
- E12 deletion request and status, permissions enforced.

Test data created per run, isolated and deterministic; no production accounts. Visual regression across supported browser/device matrix. Security tests include IDOR, CSRF, replay, rate limits, fuzzed game traces.

## 15. Acceptance criteria by feature

**Avatar:** Given a guest, when they select valid presets and save, then config persists after reload; invalid preset returns 422; stale revision returns 409; unsupported WebGL uses fallback.

**Game:** Given a valid session, when a legal trace is submitted, server returns canonical verified score and one reward; malformed/out-of-window trace rejected; retries return same result.

**Shop:** Given sufficient coins and eligible item, purchase debits once and grants once; insufficient funds leaves wallet and inventory unchanged; two concurrent purchases cannot overdraw.

**Equip:** Only owned compatible items can equip; replacing a slot does not destroy ownership; saved outfit fails gracefully if an item is no longer valid.

**Challenge:** Verified run can create one invite; link can be redeemed once by another player; expiry and block prevent participation; winner determined from verified score; no duplicate winner reward.

**Account:** Linking preserves progress and revokes guest credentials; existing-account collision does not merge data silently; data export/deletion flows are accessible.

**Admin:** Moderator can review reports but cannot change wallet; authorized adjustment requires reason and audit; all actions are logged.

## 16. Delivery backlog and release gates

**P0 vertical slice:** repo/CI, auth/guest, avatar rig and creator, one deterministic game ruleset, game session validation, wallet ledger, catalog, purchase, equip, smoke/E2E. **Gate:** E01/E02/E05/E06 pass, no known critical vulnerabilities, mobile prototype validated.

**P0 social beta:** account linking, invite challenge, quest claim, public profile, moderation, account privacy operations, telemetry, backup/restore, E03/E04/E07/E08/E09/E12. **Gate:** all critical E2E tests pass, load/security review, documented privacy policy and age policy, incident runbook.

**P1:** full friends, weekly leaderboard, My Room 3D, additional cosmetics, second mini-game, advanced achievements.

**P2:** real-time spaces, chat (only with comprehensive moderation), trading (only after economic abuse controls), monetization (requires separate legal/design review).

## 17. Open decisions requiring sign-off

1. Final age eligibility, minors handling and countries supported.
2. Whether guest sessions survive 30 or 90 days; account recovery limitations.
3. Exact 3D art direction, licensing and asset production ownership.
4. Street Hoops feel/physics tuning after a playable prototype; deterministic ruleset values in §7 (units, waypoint ranges, thresholds, segment length, streak bonus) and anti-automation thresholds are provisional.
5. Currency earning/spending balance after beta telemetry.
6. Whether public profiles default public or restricted, and privacy controls.
7. Hosting budget, target concurrency and on-call ownership.
8. Final support/retention/deletion legal wording.

**Definition of Done:** documented acceptance criteria; schema migration; authorization and validation; success/error/retry states; unit+integration tests; E2E for critical path; telemetry; accessibility and device checks; security review; rollout and rollback instructions.

## 18. Changelog

### v1.1 (2026-11-06) — review gaps closed
1. **§7 integer units:** `aimX` [-1000,1000], `power` [0,1000], `tMs` [0,59999]; thresholds 160/120/50 in these units. DTO example updated.
2. **§7 basket path:** target and ideal power depend on time (`tMs`), defined by 16 PCG32 waypoints and integer interpolation over 4,000 ms segments; draw order fixed.
3. **§7 streak bonus:** growing `min(100, 25*(streak-2))`; max score 22,900.
4. **§5/§7 trace:** `release` field removed; one event = one shot; max 93 events.
5. **§7 anti-automation:** stated the limit of replay validation; added `review_flags` signals with no automatic action in MVP.
6. **§4/§5 seed:** uint32, sent as a JSON integer (avoids JS precision loss above 2^53).
7. **§5/§6:** rematch takes `{creatorSessionId}` + Idempotency-Key and presets the recipient; game sessions start `active` (`created` state removed).
8. **§4 ledger/challenges:** reason `challenge_reward`; `creator_session_id` and `recipient_session_id` UNIQUE; `rematch_of_challenge_id`; self-challenge CHECK; `reward_coins` → `bigint`.
9. **§4/§5 blocks:** new MVP table `player_blocks` and `/me/blocks` endpoints; `blocked` removed from P1 `friendships`; block + challenge cancellation in one transaction.
10. **§2/§6/§7 ties:** stored as `completed` with `winner_player_id = NULL`.

---

**This is a detailed implementation baseline, not executable software or a verified production-ready specification.** Before sprint implementation, convert each API endpoint into OpenAPI YAML, each table into EF Core migrations, each state machine into executable tests, and game rules into deterministic shared fixtures. Resolve sign-off decisions before public launch.
