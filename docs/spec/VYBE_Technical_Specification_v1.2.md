# VYBE — Product, Game & Technical Specification v1.2

**Status:** implementation baseline / proposed decisions, not deployed or tested. v1.1 and v1.2 close the gaps found in the v1.0 and v1.1 reviews; see §18 Changelog. Values marked *provisional* need prototype validation (§17 item 4).  
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
- The recipient gets exactly one game session per challenge. If that run is `rejected` or `expired`, the challenge moves to `expired` with no winner.
- A challenge from a profile designates that profile's player as the recipient.
- Ties: challenger retains title; both receive normal gameplay rewards; no winner bonus; stored as `completed` with `winner_player_id = NULL`.
- Blocked players (either direction, `player_blocks`) cannot create or accept challenges with each other; `open` and `accepted` challenges between them are cancelled in the same transaction that applies the block. The block stays private: the blocked player sees the same response as for an invalid link, and cancelled challenges show no reason.

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
| `/challenge/:token` | Invite | accept, play, result | unavailable (invalid or blocked), redeemed, expired |
| `/profile/:handle` | Public profile | view, challenge, report | private, unavailable |
| `/settings` | Account | link, privacy, export, delete | verification required |
| `/admin` | Admin | catalog, reports, economy audit | unauthorized |

Global UI: responsive desktop sidebar/mobile bottom nav; accessible focus order; reduced-motion option; fallback 2D avatar portrait on unsupported WebGL; explicit progress indicators.

## 4. Domain model and database schema

Use PostgreSQL `uuid` IDs, `timestamptz` UTC, monetary amounts as `bigint` integer V-Coins (never float), optimistic concurrency or row locks for mutable aggregates. All tables include `created_at` where relevant; migrations are additive/expand-contract.

### Core tables

**players**: `id uuid PK`, `auth_subject text UNIQUE NULL`, `handle text NOT NULL`, `handle_normalized text UNIQUE NOT NULL`, `display_name varchar(40)`, `kind enum(guest,registered)`, `status enum(active,suspended,deleting,deleted)`, `level int NOT NULL DEFAULT 1 CHECK(level BETWEEN 1 AND 50)` (cache of the level derived from `xp_total` by §8; written in the same transaction as `xp_total`), `xp_total bigint NOT NULL DEFAULT 0`, `created_at`, `updated_at`, `deleted_at NULL`. Handle format `[a-z0-9_]{3,20}`; case-insensitive normalized uniqueness; reserved names list.

**guest_sessions**: `id uuid PK`, `player_id uuid FK`, `token_hash bytea UNIQUE`, `expires_at`, `revoked_at NULL`, `created_at`; random high-entropy token in Secure, HttpOnly, SameSite=Lax cookie; rotate on linking. Never store raw tokens.

**avatar_configs**: `player_id uuid PK/FK`, `body_preset_id text`, `skin_tone_id text`, `face_preset_id text`, `hair_preset_id text`, `hair_color_id text`, `equipped_json jsonb`, `revision bigint`, `updated_at`; validate each preset against versioned catalog, not arbitrary asset URLs.

`equipped_json` shape (also used by `saved_outfits`): `{"hair":uuid|null,"top":uuid|null,"bottom":uuid|null,"shoes":uuid|null,"emote":uuid|null,"accessories":[uuid|null,uuid|null]}`; `accessories` index = attachment point (0 or 1). An equipped `hair` item overrides `hair_preset_id` for rendering; unequipping it shows the preset again.

**catalog_items**: `id uuid PK`, `sku text UNIQUE`, `slot enum(hair,top,bottom,shoes,accessory,emote)`, `name text`, `price bigint CHECK(price>=0)`, `min_level int`, `asset_key text`, `compatible_body_presets text[]`, `rarity enum(common,rare,epic)`, `is_active bool`, `catalog_version int`, `available_from/available_until timestamptz NULL`. No arbitrary client-supplied prices. Items with `price = 0` are never sold; they are only granted (starter, achievement, admin). The shop lists only items with `price > 0`.

**player_inventory**: `player_id uuid FK`, `item_id uuid FK`, `acquired_at`, `source enum(purchase,starter_grant,achievement_grant,admin_grant) NOT NULL`, `source_transaction_id uuid FK NULL`, `source_reference_id uuid NULL` (achievement id or admin audit log id), `CHECK((source = 'purchase') = (source_transaction_id IS NOT NULL))`, `PRIMARY KEY(player_id,item_id)` for non-stackable cosmetics. Starter items are granted at onboarding with `source = starter_grant` and no ledger row.

**saved_outfits**: `id uuid PK`, `player_id uuid FK`, `name varchar(40)`, `equipped_json jsonb`, `created_at`, `updated_at`; max 3 per player in MVP; each item validated against ownership and slot compatibility.

**wallets**: `player_id uuid PK/FK`, `balance bigint NOT NULL CHECK(balance>=0)`, `revision bigint NOT NULL DEFAULT 0`, `updated_at`.

**wallet_transactions**: `id uuid PK`, `player_id uuid FK`, `delta bigint NOT NULL CHECK(delta<>0)`, `balance_after bigint NOT NULL`, `reason enum(game_reward,quest_reward,purchase,level_reward,challenge_reward,admin_adjustment)`, `reference_type text`, `reference_id uuid NULL`, `ledger_key text NOT NULL`, `created_at`; `UNIQUE(player_id,ledger_key)`; append-only permissions; indexes `(player_id,created_at DESC)`, `(reason,reference_id)`.

`ledger_key` is derived by the server, never taken raw from a client. It is the database guarantee of "exactly once":

| reason | `ledger_key` | `reference_id` |
|---|---|---|
| `purchase` | `purchase:{idempotencyKey}` | catalog item id |
| `game_reward` | `game:{sessionId}` | session id |
| `quest_reward` | `quest:{playerQuestId}` | player quest id |
| `level_reward` | `level:{level}` | NULL (`reference_type = 'level'`) |
| `challenge_reward` | `challenge:{challengeId}` | challenge id |
| `admin_adjustment` | `admin:{auditLogId}` | audit log id |

A unique violation on `ledger_key` means the effect already happened; the caller returns the stored outcome, not an error.

**game_rulesets**: `id text PK`, `game_id text`, `version int`, `config jsonb`, `checksum text`, `active_from`, `retired_at NULL`; immutable after activation. `checksum` = lowercase hex SHA-256 of the RFC 8785 (JCS) canonical JSON of `config`, computed in application code (never from Postgres `jsonb` output, which reorders keys). `retired_at` stops new solo sessions only; a retired ruleset stays playable for challenges that reference it. `config` schema: §7.

**game_sessions**: `id uuid PK`, `player_id uuid FK`, `game_id text`, `ruleset_id text FK`, `seed bigint NOT NULL CHECK(seed BETWEEN 0 AND 4294967295)` (uint32, safe as a JSON number), `status enum(created,active,submitted,verified,rejected,expired)`, `created_at`, `start_deadline` (created + 10 min, provisional), `started_at NULL` (set by the start call; time 0 for `tMs`), `submit_deadline NULL` (`started_at + durationMs + graceMs`), `submitted_at NULL`, `verified_at NULL`, `reward_day date NULL` (UTC date of `started_at`), `event_count int NULL`, `score int NULL`, `reward_coins bigint DEFAULT 0`, `reward_xp int DEFAULT 0`, `input_trace_hash text NULL`, `rejection_reason text NULL` (codes in §5), `review_flags text[] NOT NULL DEFAULT '{}'` (anti-automation signals, §7), `challenge_id uuid FK NULL` (set only on a recipient session), `version bigint`; partial `UNIQUE(challenge_id) WHERE challenge_id IS NOT NULL` (one recipient session per challenge); reward exactly once via ledger key `game:{sessionId}`. A session with `challenge_id` set is not eligible as a `creator_session_id`. Store compressed event trace separately with short retention.

**game_input_traces**: `session_id uuid PK/FK`, `compressed_trace bytea`, `encoding text`, `event_count int`, `received_at`; size cap and retention policy.

**challenges**: `id uuid PK`, `creator_player_id uuid FK`, `creator_session_id uuid FK UNIQUE` (one challenge per verified run), `recipient_player_id uuid FK NULL` (set on accept; preset for a rematch or a profile challenge), `invite_token_hash bytea UNIQUE`, `ruleset_id text FK`, `status enum(open,accepted,completed,expired,cancelled)`, `created_at`, `accept_deadline`, `accepted_at NULL`, `complete_deadline NULL`, `recipient_session_id uuid FK UNIQUE NULL` (set by compare-and-swap when the recipient creates the session), `winner_player_id uuid FK NULL` (NULL on a `completed` challenge = tie), `rematch_of_challenge_id uuid FK NULL`, `resolved_at NULL`, `version bigint`; `CHECK(recipient_player_id IS NULL OR recipient_player_id <> creator_player_id)`; indexes `(creator_player_id,created_at DESC)`, `(recipient_player_id,status)`, `(status,accept_deadline)`.

**quest_definitions**: `id uuid PK`, `code text`, `version int`, `period enum(daily,once)`, `criteria jsonb`, `target int NOT NULL CHECK(target > 0)`, `reward_coins bigint`, `reward_xp int`, `is_active bool`.

**player_quests**: `id uuid PK`, `player_id uuid FK`, `quest_id uuid FK`, `period_start date`, `progress int NOT NULL DEFAULT 0`, `status enum(active,completed,claimed)`, `completed_at NULL`, `claimed_at NULL`, `UNIQUE(player_id,quest_id,period_start)`.

**player_daily_counters**: `player_id uuid FK`, `day date` (UTC), `runs_rewarded int NOT NULL DEFAULT 0`, `xp_from_games int NOT NULL DEFAULT 0`, `challenge_bonuses int NOT NULL DEFAULT 0`, `PRIMARY KEY(player_id,day)`. Updated only by atomic `INSERT … ON CONFLICT DO UPDATE … RETURNING` in the same transaction as the reward it limits (§7).

**achievements**: `id uuid PK`, `code text UNIQUE`, `criteria jsonb`, `reward_item_id uuid NULL`; **player_achievements**: `(player_id,achievement_id) PK`, `unlocked_at`.

**friendships** (P1): `requester_id`, `addressee_id`, `status enum(pending,accepted,declined)`, timestamps; canonical pair unique and no self-relations. MVP may launch with challenge links before full friendship UI. Blocking is not a friendship state; it lives in `player_blocks`.

**player_blocks** (MVP): `blocker_player_id uuid FK`, `blocked_player_id uuid FK`, `created_at`, `PRIMARY KEY(blocker_player_id,blocked_player_id)`, `CHECK(blocker_player_id <> blocked_player_id)`; index `(blocked_player_id)` for reverse lookup. A block in either direction prevents challenges between the pair. Private: the blocked player is not told.

**player_reports**: `id uuid PK`, `reporter_player_id uuid FK`, `target_player_id uuid FK`, `reason enum(offensive_name,cheating,harassment,other)`, `details varchar(500) NULL`, `status enum(open,actioned,dismissed)`, `created_at`, `reviewed_at NULL`, `reviewed_by text NULL`; restricted access.

**admin_audit_logs**: `id`, `actor_subject`, `action`, `entity_type`, `entity_id`, `before_json`, `after_json`, `reason`, `created_at`; append-only and restricted.

**idempotency_records**: `player_id`, `operation`, `key`, `request_hash`, `status enum(in_progress,completed)`, `response_status int NULL`, `response_body jsonb NULL`, `created_at`, `expires_at` (created + 72 h), unique `(player_id,operation,key)`. Protocol:
1. `INSERT … ON CONFLICT DO NOTHING` with `status = in_progress` in its own short transaction.
2. If a row already exists: different `request_hash` → 422 `idempotency_key_mismatch`; `in_progress` → 409 `request_in_progress` (client retries later); `completed` → return the stored response.
3. Execute the operation; then store the response and set `completed`. If the operation fails before commit, delete the row so the key can be retried.
Clients must not reuse a key after 24 h. The ledger `UNIQUE(player_id,ledger_key)` remains the final guard if a record expired.

### Database invariants
1. Every wallet debit and corresponding inventory grant commit in one DB transaction.
2. Wallet row locked (`SELECT ... FOR UPDATE`) for debits/credits; `CHECK(balance>=0)` as defense in depth.
3. Rewards use the server-derived `ledger_key` (unique per player), never a client-supplied amount or raw client key.
4. Equipping requires owned, active/usable and compatible item; one item per slot, and one per accessory attachment point (max 2).
5. Challenge transitions use compare-and-swap version/status predicate to avoid double acceptance.
6. Game sessions and rulesets are immutable after verification; audit corrections use compensating entries.
7. Retention and cascade policies are explicitly tested before deletion features ship.
8. Inserting a `player_blocks` row and cancelling the pair's `open`/`accepted` challenges commit in one DB transaction.
9. Challenge winner bonus uses ledger reason `challenge_reward` with `ledger_key = challenge:{challengeId}`; at most one per challenge.
10. Run verification, daily counter update, reward ledger rows, XP/level update and quest progress commit in one DB transaction.
11. Quest progress uses atomic upsert (`INSERT … ON CONFLICT (player_id,quest_id,period_start) DO UPDATE SET progress = LEAST(target, player_quests.progress + :delta)`); never read-modify-write.

## 5. API contracts v1

Base path `/api/v1`. JSON UTF-8; UTC ISO 8601; UUID strings; `Idempotency-Key` required for state-changing economic actions. Auth: validated Supabase bearer token for registered users or signed guest cookie for guests. `traceparent` propagated. Errors: RFC 9457 ProblemDetails with `type`, `title`, `status`, `detail`, `instance`, `code`, `traceId`. Never leak internal exception details. `code` is a stable machine-readable string; each endpoint below lists its codes, and the OpenAPI document enumerates them.

**Common codes (all endpoints):** 400 `malformed_request` (unparseable JSON or wrong JSON types), 401 `unauthenticated`, 403 `forbidden`, 403 `account_suspended` (economic actions and challenge creation), 404 `not_found`, 409 `revision_conflict`, 409 `request_in_progress`, 413 `payload_too_large`, 422 `validation_failed` (field errors in `errors[]`), 422 `idempotency_key_mismatch`, 428 `idempotency_key_required`, 429 `rate_limited`.

### Identity / avatar
- `POST /guests` → `201 {playerId,handle,guestExpiresAt}` + secure guest cookie; rate limited.
- `POST /accounts/link` `{linkProof}` → `200 {playerId,accountLinked:true}`; proof must be verified server-side; 409 `identity_already_linked`.
- `GET /me` → `200 {id,handle,level,xpTotal,walletBalance,avatarRevision}`.
- `GET /me/avatar` → `200 {revision,bodyPresetId,skinToneId,facePresetId,hairPresetId,hairColorId,equipped}`; `equipped` uses the `equipped_json` shape (§4).
- `PUT /me/avatar` `{bodyPresetId,skinToneId,facePresetId,hairPresetId,hairColorId,expectedRevision}` → `200 {revision,avatar}`; 409 `revision_conflict`; 422 `invalid_preset`; 409 `incompatible_equipment` with `itemIds[]` when a body preset change leaves equipped items incompatible (nothing changes; the client unequips them first).
- `GET /profiles/{handle}` → public fields only; 404 `not_found` if unavailable.

### Safety
- `POST /me/blocks` `{handle}` → `204`; idempotent; cancels pending challenges between the pair (invariant 8); 404 `not_found`; 422 `self_block`.
- `DELETE /me/blocks/{handle}` → `204`; idempotent; does not restore cancelled challenges.
- `GET /me/blocks?cursor=&limit=` → players the caller blocked; never exposes who blocked the caller.
- `POST /reports` `{targetHandle,reason,details?}` → `202 {reportId}`; `reason` from `player_reports.reason`; 404 `not_found`; 422 `self_report`; rate limited (provisional: 10/day per reporter).

### Catalog / inventory / economy
- `GET /catalog/items?slot=top&cursor=...&limit=24` → `{items:[{id,sku,name,price,slot,rarity,minLevel,previewUrl,owned}],nextCursor}`; only active items with `price > 0` in their availability window; server-generated asset URLs.
- `GET /me/inventory` → owned item IDs and metadata, including `source`.
- `POST /purchases` `{itemId}` + Idempotency-Key → `200 {transactionId,itemId,balanceAfter}`; 409 `insufficient_funds`; 409 `already_owned`; 403 `level_too_low`; 422 `item_unavailable` (inactive, outside window or unknown); 422 `item_not_purchasable` (`price = 0`).
- `POST /me/equipment` `{slot,itemId|null,attachIndex?,expectedAvatarRevision}` → `200 {revision,equipped}`; `attachIndex` (0 or 1) is required for `slot = accessory` and forbidden otherwise; 403 `item_not_owned`; 422 `incompatible_item` (slot or body preset); 422 `item_inactive`; 409 `revision_conflict`.
- `GET /me/outfits` → `200 {outfits:[{id,name,equipped,valid,invalidItemIds[]}]}`.
- `POST /me/outfits` `{name}` → `201 {id}`; saves the current equipment; 409 `outfit_limit_reached` (max 3).
- `PUT /me/outfits/{id}` `{name}` → `200`; `DELETE /me/outfits/{id}` → `204`.
- `POST /me/outfits/{id}/apply` `{expectedAvatarRevision}` → `200 {revision,equipped}`; all-or-nothing; 422 `outfit_items_invalid` with `itemIds[]` (no longer owned, active or compatible); 409 `revision_conflict`.
- `GET /me/wallet/transactions?cursor=&limit=` → paginated read-only ledger.

### Gameplay
- `GET /games/rulesets/{id}` → `200 {id,gameId,version,config,checksum}`; immutable, `Cache-Control: public, max-age=31536000, immutable`. The client verifies `checksum` before it uses `config`.
- `POST /games/street-hoops/sessions` `{challengeId?:uuid}` → `201 {sessionId,status:"created",seed,rulesetId,rulesetChecksum,startDeadline}`. `seed` is a JSON integer in uint32 range. Without `challengeId`: the current active ruleset and a fresh seed. With `challengeId`: the response carries the creator run's `seed` and `rulesetId`; only the bound recipient of an `accepted` challenge may call it, before `complete_deadline`; the server sets `challenges.recipient_session_id` by compare-and-swap. Codes: 404 `challenge_unavailable` (unknown, or caller is not the recipient, or a block exists); 409 `challenge_session_exists`; 410 `challenge_expired`.
- `POST /games/sessions/{id}/start` → `200 {startedAt,submitDeadline}`; moves `created -> active`; the client calls it after assets are loaded and the countdown ends, and sets its local time 0 when the response arrives. Idempotent (a repeat returns the same values). 410 `session_expired` after `startDeadline`.
- `POST /games/sessions/{id}/submit` `{rulesetId,events:[{tMs,aimX,power}],clientReportedScore}` + Idempotency-Key. Each event is one shot. Responses:
  - `202 {sessionId,status:"submitted"}` (asynchronous verification; poll `GET`).
  - `200 {status:"verified",score,rewards:{coins,xp,levelAfter}}`.
  - `200 {status:"rejected",rejectionReason}`; `rejected` is terminal. `rejectionReason` ∈ `not_started`, `ruleset_mismatch`, `invalid_event` (non-integer or missing field), `out_of_range`, `non_monotonic`, `cadence_violation`, `too_many_events`, `submitted_too_early`, `submitted_too_late`.
  - Any parseable body with wrong content is a rejection, never a 400: a failed check ends the session. 400 is only for unparseable JSON; 413 for a body above the size cap (session stays `active` until `submit_deadline`).
  - Client score is diagnostic only; server recomputes.
- `GET /games/sessions/{id}` → state, `rejectionReason` and verified score/rewards; no trace exposed to others.

### Challenges
- `POST /challenges` `{creatorSessionId,recipientHandle?}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`. The creator session must be the caller's, `verified`, not a recipient session, have at least `config.minShotsForRewards` events, and not be used for another challenge. `recipientHandle` presets the recipient (profile challenge). Codes: 422 `session_not_eligible`; 409 `session_already_used`; 404 `not_found` for an unknown handle or a block in either direction.
- `GET /challenges/by-token/{token}` → safe public challenge preview, no secret token hash.
- `POST /challenges/by-token/{token}/accept` + Idempotency-Key → `200 {challengeId,status,completeDeadline}`; atomic token redemption. Codes: 404 `challenge_unavailable` (unknown token, preset recipient is someone else, or a block exists in either direction; one code so the response does not reveal a block); 403 `self_accept`; 409 `challenge_already_accepted`; 410 `challenge_expired`; 410 `challenge_cancelled`. The token and block checks run before all status checks, and a challenge cancelled by a block returns 404 `challenge_unavailable`, so a blocked player cannot detect the block.
- `GET /challenges/{id}` → participants and status, filtered by privacy rules; a challenge cancelled by a block shows `cancelled` with no reason.
- `GET /me/challenges?role=created|received&status=&cursor=&limit=` → caller's challenges, newest first.
- `POST /challenges/{id}/rematch` `{creatorSessionId}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`. Original challenge must be `completed`; caller must be one of its participants and becomes the new creator; the other participant becomes the preset `recipient_player_id`; `creatorSessionId` follows the same rules as `POST /challenges`; cannot recycle prior result. Codes: as `POST /challenges`; 404 `challenge_unavailable` if a block exists; 409 `challenge_not_completed`.

### Quests and account
- `GET /me/quests` → `200 {quests:[{playerQuestId,code,periodStart,progress,target,status,rewardCoins,rewardXp,claimableUntil}]}`; includes completed, unclaimed quests from earlier periods while claimable.
- `POST /me/quests/{playerQuestId}/claim` + Idempotency-Key → `200 {coinsGranted,xpGranted,level}`. A completed quest stays claimable for 7 days after `period_start` (provisional). Codes: 409 `quest_not_completed`; 409 `quest_already_claimed`; 410 `quest_claim_expired`.
- `POST /me/data-export` → `202 {exportId}`; `GET /me/data-export/{exportId}` → `{status:queued|running|ready|failed, downloadUrl?, downloadExpiresAt?}`; `downloadUrl` is short-lived and single-player.
- `POST /me/deletion-requests` → `202 {requestId}`, requires reauthentication for registered account (403 `reauthentication_required`); `GET /me/deletion-requests/{requestId}` → `{status:queued|processing|completed,completedAt?}`.

### Admin (base `/api/v1/admin`; moderator or administrator claim)
Every write requires `reason` and writes `admin_audit_logs` in the same transaction.
- `GET /reports?status=open&cursor=` (moderator); `POST /reports/{id}/resolve` `{status:actioned|dismissed,reason}` (moderator).
- `POST /players/{id}/suspension` `{reason,until?}`; `DELETE /players/{id}/suspension` `{reason}` (moderator).
- `PATCH /catalog/items/{id}` `{isActive,reason}` (administrator).
- `POST /wallet-adjustments` `{playerId,delta,reason}` + Idempotency-Key (administrator only; ledger key `admin:{auditLogId}`); 409 `insufficient_funds` if the result is negative.
- `GET /audit-logs?entityType=&entityId=&actor=&cursor=` (administrator).

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
`created -> active -> submitted -> verified | rejected`; `created -> expired` (not started before `start_deadline`); `active -> expired` (no submission before `submit_deadline`); terminal states `verified,rejected,expired`. `POST …/start` moves `created -> active` and sets `started_at` (time 0 for `tMs`) and `submit_deadline`. Server clock is authoritative. Time checks at submit (values from `config`):
- `submitted_at > submit_deadline` → `rejected` (`submitted_too_late`).
- `submitted_at < started_at + max(tMs) - earlyToleranceMs` → `rejected` (`submitted_too_early`); this stops traces that claim more game time than has passed.

A recipient session that ends `rejected` or `expired` moves its challenge `accepted -> expired` in the same transaction. `submitted` may be processed synchronously or asynchronously, but repeated submissions must return the canonical result. Only verified sessions may produce rewards or challenges.

### Challenge
`open -> accepted -> completed`; `open -> expired|cancelled`; `accepted -> expired|cancelled`; terminal `completed,expired,cancelled`. Acceptance binds one recipient; completion references one verified run under the challenge ruleset. `completed` with `winner_player_id = NULL` is a tie. A block in either direction moves `open|accepted -> cancelled`. `accepted -> expired` also when the recipient run is `rejected` or `expired`. The deadline check uses the recipient session's `started_at`: a run started before `complete_deadline` may finish after it, within its own `submit_deadline`. A scheduled sweeper expires overdue records idempotently; read endpoints also check deadline to avoid stale acceptance.

### Quest
`active -> completed -> claimed`; `completed` is set when `progress` reaches `target`. Period rollover creates a new record, never resets claimed history. Server derives progress from verified domain events with the atomic upsert of invariant 11; rows are created lazily by the same upsert. A completed quest is claimable for 7 days after `period_start` (provisional).

### Purchase
`requested -> validated -> committed` (atomic debit + inventory grant + ledger); validation failures return no state change. Duplicate request key returns stored outcome; a concurrent duplicate returns 409 `request_in_progress`; key reuse with different body returns 422 `idempotency_key_mismatch` (protocol in `idempotency_records`, §4).

### Account
`guest -> registered -> suspended? -> deleting -> deleted`; a guest token cannot be used after linking. Suspension blocks economic actions and challenge creation.

## 7. Street Hoops game ruleset v1

**Objective:** score maximum points in 60 seconds. Input can be mouse, touch or keyboard; the client quantizes it to the integer units below before it records an event.

**Input units (normative, all integers):**
- `aimX` in [-1000, 1000] (1000 = full court half-width).
- `power` in [0, 1000].
- `tMs` in [0, 59999], milliseconds since the start call (`started_at` on the server; arrival of the start response on the client).
- The trace contains **shot events only** (one event = one release). Aim/power samples between shots are not sent. Non-integer or out-of-range values => `rejected`.

**Ruleset config (`game_rulesets.config` for `street-hoops-v1`; all values integers, provisional):**

| Key | Value | Meaning |
|---|---|---|
| `streamId` | 54 | PCG32 stream selector, uint32 |
| `durationMs` | 60000 | game time |
| `graceMs` | 5000 | upload allowance after `durationMs`; never extends game time |
| `earlyToleranceMs` | 2000 | clock tolerance for `submitted_too_early` |
| `segmentMs` | 4000 | basket path segment; `durationMs mod segmentMs` must be 0 |
| `xMin`, `xMax` | -700, 700 | waypoint `x` range |
| `pMin`, `pMax` | 300, 900 | waypoint ideal power range |
| `hitDx`, `hitDp` | 160, 120 | hit thresholds |
| `perfectDx`, `perfectDp` | 50, 50 | perfect thresholds |
| `hitPoints`, `perfectPoints` | 100, 150 | points per shot |
| `streakStart`, `streakStep`, `streakMax` | 3, 25, 100 | streak bonus |
| `minGapMs` | 650 | minimum gap between shots |
| `maxEvents` | 93 | maximum shots |
| `minShotsForRewards` | 5 | minimum shots for rewards, quests and challenges |

Derived: `waypointCount = durationMs / segmentMs + 1` (= 16). The client gets `config` from `GET /games/rulesets/{id}` and never hard-codes these values.

**Rules:**
- Session duration `durationMs`, server-authorized start (`POST …/start`) with `graceMs` for network upload; the grace never extends game simulation.
- Cadence: `tMs` strictly increasing; gap between consecutive events >= `minGapMs`; at most `maxEvents` events (0, 650, …, 59,800). Reject traces exceeding configured limits.
- **PRNG:** PCG32 (XSH-RR output, 64-bit LCG state), seeded with the reference `pcg32_srandom(initstate = seed, initseq = streamId)` from pcg-random.org; `seed` and `streamId` are uint32 widened to uint64. `next()` returns an unsigned 32-bit integer (C# `uint`; JS `Number` in [0, 2^32 - 1] after `BigInt` arithmetic). Draw an integer in [lo, hi] as `lo + (next() mod (hi - lo + 1))` with unsigned `mod`; the small modulo bias is accepted. The JS client uses `BigInt` for the 64-bit state.
- **Basket path:** the timeline has `durationMs / segmentMs` (= 15) segments. Before the run, draw `waypointCount` (= 16) waypoints **interleaved**: for `k = 0, 1, …, 15`, draw `x_k` in [`xMin`, `xMax`], then `p_k` in [`pMin`, `pMax`]. The draw sequence is therefore `x_0, p_0, x_1, p_1, …, x_15, p_15` (32 draws). No other draws occur. For time `t`: `k = t div segmentMs`, `f = t mod segmentMs`, `targetX(t) = x_k + trunc((x_{k+1} - x_k) * f / segmentMs)`, `idealPower(t) = p_k + trunc((p_{k+1} - p_k) * f / segmentMs)`. `trunc` rounds toward zero (C# integer `/`; JS `Math.trunc`). Intermediate values stay below 2^31.
- **Shot evaluation:** `dx = |aimX - targetX(tMs)|`, `dp = |power - idealPower(tMs)|`. Perfect when `dx <= perfectDx` and `dp <= perfectDp`; hit when `dx <= hitDx` and `dp <= hitDp`; otherwise miss.
- **Points:** hit = `hitPoints`, perfect = `perfectPoints`, miss = 0. `streak` = number of consecutive hits including the current shot (a perfect is a hit). Streak bonus = `min(streakMax, streakStep * (streak - streakStart + 1))` when `streak >= streakStart`, else 0. With v1 values this is `min(100, 25 * (streak - 2))`: the bonus grows +25, +50, +75, then stays at +100. A miss resets `streak` to 0. Maximum score = 22,900 (93 consecutive perfects).
- Server replays the full trace, validates the time window (§6), `rulesetId`, units, monotonic timestamps, event count and cadence, then computes the canonical score. Invalid trace => `rejected` with the first failing `rejectionReason` in this order: `not_started`, `submitted_too_late`, `ruleset_mismatch`, `invalid_event`, `out_of_range`, `too_many_events`, `non_monotonic`, `cadence_violation`, `submitted_too_early`. No reward.
- **Golden fixtures:** `packages/game-rules/fixtures/*.json` holds `{rulesetId, config, seed, waypoints, events, expected:{shots:[{targetX,idealPower,outcome,points}], score}}` plus PCG32 reference vectors, plus rejection fixtures `{events, expected:{rejectionReason}}` for every trace-level reason. The C# server and the TS client must both pass every fixture in CI.
- Client animations may use smooth Three.js physics for appearance, but the shown outcome must match the deterministic result. Prototype the feel before freezing numeric thresholds.

**Anti-automation (limit of replay validation):** the client knows the `seed` and must render the basket, so a bot can compute a perfect trace. Replay proves a trace is legal, not that a human played. MVP records signals in `game_sessions.review_flags` and takes **no automatic action**: perfect-shot rate above threshold in a run; low variance of inter-shot gaps; low variance of `dx`/`dp`; run volume at the daily cap over many consecutive days. Flagged runs stay verified and rewarded; flags feed the admin review queue. Thresholds are set from prototype and beta telemetry.

**Rewards per verified run:** base `coins = min(150, 20 + floor(score/500)*10)` and `xp = min(80, 10 + floor(score/400)*5)`. All rewards computed on server in the verification transaction (invariant 10); no reward for rejected runs. These are tuning defaults, not measured economy balance.
- **Reward day:** `reward_day` = UTC date of `started_at`, so late asynchronous verification after 00:00 UTC does not move a run to the next day. Daily reset UTC, explicitly shown in UI.
- **Minimum play:** a verified run with fewer than `minShotsForRewards` events grants 0 coins and 0 XP, makes no quest progress, does not count in `runs_rewarded`, and cannot create a challenge.
- **Run cap:** increment `player_daily_counters.runs_rewarded` for (`player_id`, `reward_day`) with `INSERT … ON CONFLICT DO UPDATE … RETURNING`; the returned value `n` is this run's number. `n <= 10` → full coins; `11 <= n <= 20` → `floor(coins / 4)`; `n > 20` → 0 coins. Concurrent runs get distinct `n`.
- **XP cap:** `grant = min(xp, 800 - xp_from_games)` using the locked counter row, so the last run fills exactly to 800 (example: 790 + 80 grants 10). Then `xp_from_games += grant`.
- A ledger row is written only when coins > 0 (`CHECK(delta<>0)`).

**Challenge rules:** creator run's immutable ruleset and seed define recipient's conditions; recipient uses separate session. Winner has higher canonical score; ties favor creator for display only and store `winner_player_id = NULL`. Winner receives +50 V-Coins (ledger reason `challenge_reward`) once per challenge, capped at 5 winner bonuses/day via `player_daily_counters.challenge_bonuses` for the UTC date of `resolved_at` (same atomic upsert pattern); standard gameplay rewards remain separate. No stake, no currency transfer between players.

## 8. XP, quests, catalog and economy

**XP thresholds:** Level 1 starts at 0; total XP required to reach level `L` (L>=1) = `100*(L-1)^2`. Level 50 at 240,100 XP; level 50 is the cap (XP keeps accumulating, level stays 50). Persist `xp_total`; the domain service derives the level and writes the cached `players.level` in the same transaction; no client authority. Level-up grants 100 V-Coins once per new level via ledger source `level_reward` with `ledger_key = level:{L}`. One XP grant that crosses several levels grants one reward per level. Quest XP is not limited by the 800/day gameplay cap.

**Daily quests (UTC):**
1. `play_3`: finish 3 reward-eligible verified runs (`target` 3) → 100 coins + 60 XP.
2. `score_1500`: achieve >=1,500 points in one verified run (`target` 1) → 100 coins + 60 XP.
3. `challenge_or_streak`: finish one accepted challenge **or** reach a streak of 5 hits in one verified run (`target` 1) → 100 coins + 60 XP. The solo path keeps the quest open to players without friends; quests never require inviting new people.

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
- PCG32 reference vectors, interleaved draw order, basket path interpolation (incl. negative deltas), streak bonus, cross-language fixture parity (C# and TS), one rejection fixture per trace-level `rejectionReason`, ruleset checksum (RFC 8785) parity.
- Reward run number tiers (10/20), XP partial fill to 800, minimum-shots rule, multi-level XP grant, level cap 50.
- XP threshold, rewards cap, score replay, combo, catalog compatibility, challenge winner/tie, quest period boundary, idempotency request-hash handling.

### Integration (real PostgreSQL)
- Concurrent purchases with insufficient funds; duplicate purchase retry; reward exactly once; account link conflict; challenge double accept; expired challenge; block cancels pending; audit trail; authorization boundaries; migrations up from clean DB.
- Two submits of the same session with different Idempotency-Keys → one `game:{sessionId}` ledger row; concurrent same-key requests → one execution and 409 `request_in_progress`; two runs verified concurrently at the 10th slot → distinct run numbers; concurrent quest progress → no lost increments; second recipient session for one challenge → 409 `challenge_session_exists`; rejected recipient run → challenge `expired`; blocked accept → 404 `challenge_unavailable`; free item purchase → 422 `item_not_purchasable`.

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
4. Street Hoops feel/physics tuning after a playable prototype; deterministic ruleset values in §7 (units, all `config` values incl. `minShotsForRewards` and `earlyToleranceMs`) and anti-automation thresholds are provisional. Also provisional: session `start_deadline` 10 min, quest claim window 7 days, report rate limit 10/day.
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

### v1.2 (2026-11-06) — v1.1 review findings closed
1. **§4 exactly-once rewards:** ledger column renamed `idempotency_key` → `ledger_key`, derived by the server per reason (`game:{sessionId}`, `quest:{playerQuestId}`, `level:{L}`, `challenge:{id}`, `purchase:{key}`, `admin:{auditLogId}`); `reference_id` nullable; `player_quests` gets a uuid `id`.
2. **§4/§5 free items:** `price = 0` items are never sold (422 `item_not_purchasable`) and not listed; `player_inventory.source` enum and grant references added.
3. **§4/§7 daily caps:** new `player_daily_counters`; reward day = UTC date of `started_at`; atomic run numbering; XP fills exactly to the cap.
4. **§4/§5/§6 session clock:** `created` state restored with `POST /games/sessions/{id}/start`; `start_deadline`, `submit_deadline`; `submitted_too_early` / `submitted_too_late` checks. Reverses v1.1 item 7 (sessions starting `active`).
5. **§5 submit outcomes:** `200 {status:"rejected",rejectionReason}` with an enumerated reason list and fixed check order; 400 only for unparseable JSON.
6. **§4/§5/§7 ruleset delivery:** `GET /games/rulesets/{id}`; full `config` table; checksum = SHA-256 over RFC 8785 JSON; retired rulesets stay playable for challenges.
7. **§2/§4/§5/§6 recipient session:** only the bound recipient creates it; one per challenge (partial UNIQUE); CAS on `recipient_session_id`; rejected/expired run → challenge `expired`; deadline uses `started_at`.
8. **§5 missing endpoints:** `GET /me/avatar`, saved outfits CRUD + apply, `POST /reports`, `GET /me/challenges`, `recipientHandle` on `POST /challenges`, export and deletion status, admin endpoints.
9. **§7 PCG32:** interleaved draw order stated; `streamId` uint32 (= 54); unsigned `next()` and `mod`; `waypointCount` derived from `durationMs/segmentMs`.
10. **§7 minimum play:** `minShotsForRewards` (5) for rewards, quest progress and challenge eligibility.
11. **§4/§6 idempotency:** `in_progress`/`completed` protocol; 409 `request_in_progress`; key mismatch → 422 `idempotency_key_mismatch`.
12. **§2/§3/§5 block privacy:** blocks return the same 404 `challenge_unavailable` as an invalid link; cancelled challenges show no reason. Fixes a contradiction added in v1.1.
13. **§4/§5 equipment:** `equipped_json` shape with 2 accessory points; `attachIndex`; hair item overrides hair preset; body preset change with incompatible items → 409.
14. **§4/§5/§6/§8 quests:** claim by `playerQuestId`; 7-day claim window; atomic progress upsert; quest 3 becomes `challenge_or_streak` (solo path); `reward_coins` → `bigint`.
15. **§4/§5/§8 errors and level:** common and per-endpoint `code` lists; `players.level` is a cached column, capped at 50.

---

**This is a detailed implementation baseline, not executable software or a verified production-ready specification.** Before sprint implementation, convert each API endpoint into OpenAPI YAML, each table into EF Core migrations, each state machine into executable tests, and game rules into deterministic shared fixtures. Resolve sign-off decisions before public launch.
