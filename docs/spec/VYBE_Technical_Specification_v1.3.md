# VYBE — Product, Game & Technical Specification v1.4

**Status:** implementation baseline / proposed decisions, not deployed or tested. v1.1, v1.2 and v1.3 close the gaps found in the v1.0, v1.1 and v1.2 reviews; v1.4 applies the signed §17 decisions; see §18 Changelog. Values marked *provisional* need prototype validation (§17 item 4).  
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
- Blocked players (either direction, `player_blocks`) cannot create or accept challenges with each other; `open` and `accepted` challenges between them are cancelled in the same transaction that applies the block. The block stays private: the blocked player sees the same response as for an invalid link, block-cancelled challenges are shown as `expired`, and a challenge to a player who blocked the creator is silently suppressed (§5). Accepted residual risk: a determined player can still infer a block (signed-in vs anonymous invite preview; a challenge that expires before its deadline).

### J3: account linking
Guest → verify email/OAuth identity via Supabase Auth → backend binds existing player to verified subject in atomic operation → revoke guest credentials → keep player ID, wallet, inventory and history. If verified identity is already linked to another player, **do not auto-merge**; require explicit account selection/support flow.

### J4: deletion
Request deletion → reauthentication for registered users → revoke sessions → queue deletion workflow → anonymize the player (the ledger is append-only, so the player row stays: `status = deleted`, `deleted_at` set, handle replaced by the reserved pseudonym `d_` + 18 random `[a-z0-9]`, personal fields cleared) and delete other data according to documented retention/legal obligations → return confirmation and status. Preserve minimal fraud/audit data only with documented lawful basis and retention window; never promise immediate total erasure of backups.

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
| `/challenges` | My challenges | create, open, rematch | empty, expired |
| `/challenge/:token` | Invite | accept, play, result | unavailable (invalid or blocked), redeemed, expired |
| `/profile/:handle` | Public profile | view, challenge, report | private, unavailable |
| `/settings` | Account | link, privacy, export, delete | verification required |
| `/admin` | Admin | catalog, reports, economy audit | unauthorized |

Global UI: responsive desktop sidebar/mobile bottom nav; accessible focus order; reduced-motion option; fallback 2D avatar portrait on unsupported WebGL; explicit progress indicators.

## 4. Domain model and database schema

Schema `vybe` (not `public`; ADR 0002). Use PostgreSQL `uuid` IDs (UUIDv7), `timestamptz` UTC, enum columns as `text` with CHECK constraints, monetary amounts as `bigint` integer V-Coins (never float), optimistic concurrency or row locks for mutable aggregates. All tables include `created_at` where relevant; migrations are additive/expand-contract.

### Core tables

**players**: `id uuid PK`, `auth_subject text UNIQUE NULL`, `handle text NOT NULL`, `handle_normalized text UNIQUE NOT NULL`, `display_name varchar(40)`, `kind enum(guest,registered)`, `status enum(active,suspended,deleting,deleted)`, `level int NOT NULL DEFAULT 1 CHECK(level BETWEEN 1 AND 50)` (cache of the level derived from `xp_total` by §8; written in the same transaction as `xp_total`), `xp_total bigint NOT NULL DEFAULT 0`, `suspended_until timestamptz NULL` (NULL with `status = suspended` = indefinite), `suspension_reason text NULL`, `created_at`, `updated_at`, `deleted_at NULL`. A player is suspended when `status = suspended` and (`suspended_until IS NULL` or `suspended_until > now()`); an elapsed suspension is treated as `active` on read and reset to `active` by the sweeper. Handle format `[a-z0-9_]{3,20}`, stored and returned in lowercase (`CHECK(handle_normalized = handle)`); input handles are matched case-insensitively; reserved names list.

**guest_sessions**: `id uuid PK`, `player_id uuid FK`, `token_hash bytea UNIQUE`, `expires_at`, `revoked_at NULL`, `created_at`; random high-entropy token in a `__Host-vybe_guest` cookie (Secure, HttpOnly, SameSite=Lax, Path=/); revoked on linking (the link response clears the cookie). Never store raw tokens.

**avatar_configs**: `player_id uuid PK/FK`, `body_preset_id text`, `skin_tone_id text`, `face_preset_id text`, `hair_preset_id text`, `hair_color_id text`, `equipped_json jsonb`, `revision bigint`, `updated_at`; validate each preset against versioned catalog, not arbitrary asset URLs.

`equipped_json` shape (also used by `saved_outfits`): `{"hair":uuid|null,"top":uuid|null,"bottom":uuid|null,"shoes":uuid|null,"emote":uuid|null,"accessories":[uuid|null,uuid|null]}`; `accessories` index = attachment point (0 or 1). An equipped `hair` item overrides `hair_preset_id` for rendering; unequipping it shows the preset again.

**catalog_items**: `id uuid PK`, `sku text UNIQUE`, `slot enum(hair,top,bottom,shoes,accessory,emote)`, `name text`, `price bigint CHECK(price>=0)`, `min_level int`, `asset_key text`, `compatible_body_presets text[] CHECK(cardinality >= 1)`, `rarity enum(common,rare,epic)`, `is_active bool`, `catalog_version int`, `available_from/available_until timestamptz NULL`. No arbitrary client-supplied prices. Items with `price = 0` are never sold; they are only granted (starter, achievement, admin). The shop lists only items with `price > 0`.

**player_inventory**: `player_id uuid FK`, `item_id uuid FK`, `acquired_at`, `source enum(purchase,starter_grant,achievement_grant,admin_grant) NOT NULL`, `source_transaction_id uuid FK NULL`, `source_reference_id uuid NULL` (achievement id or admin audit log id), `CHECK((source = 'purchase') = (source_transaction_id IS NOT NULL))`, `PRIMARY KEY(player_id,item_id)` for non-stackable cosmetics. Starter items are granted at onboarding with `source = starter_grant` and no ledger row.

**saved_outfits**: `id uuid PK`, `player_id uuid FK`, `slot smallint CHECK(slot BETWEEN 1 AND 3)` with `UNIQUE(player_id,slot)` (enforces the limit), `name varchar(40)`, `equipped_json jsonb`, `created_at`, `updated_at`; max 3 per player in MVP; each item validated against ownership and slot compatibility.

**wallets**: `player_id uuid PK/FK`, `balance bigint NOT NULL CHECK(balance>=0)`, `revision bigint NOT NULL DEFAULT 0`, `updated_at`.

**wallet_transactions**: `id uuid PK`, `player_id uuid FK`, `delta bigint NOT NULL CHECK(delta<>0)`, `balance_after bigint NOT NULL`, `reason enum(game_reward,quest_reward,purchase,level_reward,challenge_reward,admin_adjustment)`, `reference_type text`, `reference_id uuid NULL`, `ledger_key text NOT NULL`, `created_at`; `UNIQUE(player_id,ledger_key)`; append-only (trigger rejects UPDATE, DELETE, TRUNCATE); indexes `(player_id,created_at DESC)`, `(reason,reference_id)`.

`ledger_key` is built by the server. Client input appears in it only as a validated UUID `Idempotency-Key` (purchase, admin adjustment). It is the database guarantee of "exactly once":

| reason | `ledger_key` | `reference_id` |
|---|---|---|
| `purchase` | `purchase:{itemId}` (one purchase per non-stackable item) | catalog item id |
| `game_reward` | `game:{sessionId}` | session id |
| `quest_reward` | `quest:{playerQuestId}` | player quest id |
| `level_reward` | `level:{level}` | NULL (`reference_type = 'level'`) |
| `challenge_reward` | `challenge:{challengeId}` | challenge id |
| `admin_adjustment` | `admin:{idempotencyKey}` | audit log id |

A unique violation on `ledger_key` means the effect already happened; the caller returns the stored outcome, not an error.

**game_rulesets**: `id text PK`, `game_id text`, `version int`, `config jsonb`, `checksum text`, `active_from`, `retired_at NULL`; immutable after activation. `checksum` = lowercase hex SHA-256 of the RFC 8785 (JCS) canonical JSON of `config`, computed in application code (never from Postgres `jsonb` output, which reorders keys). `retired_at` stops new solo sessions only; a retired ruleset stays playable for challenges that reference it. `config` schema: §7.

**game_sessions**: `id uuid PK`, `player_id uuid FK`, `game_id text`, `ruleset_id text FK`, `seed bigint NOT NULL CHECK(seed BETWEEN 0 AND 4294967295)` (uint32, safe as a JSON number), `status enum(created,active,submitted,verified,rejected,expired)`, `created_at`, `start_deadline` (created + 10 min, provisional), `started_at NULL` (set by the start call; time 0 for `tMs`), `submit_deadline NULL` (`started_at + durationMs + graceMs`), `submitted_at NULL`, `verified_at NULL`, `reward_day date NULL` (UTC date of `started_at`), `event_count int NULL`, `score int NULL`, `reward_coins bigint DEFAULT 0`, `reward_xp int DEFAULT 0`, `input_trace_hash text NULL`, `rejection_reason text NULL` (codes in §5), `review_flags text[] NOT NULL DEFAULT '{}'` (anti-automation signals, §7), `challenge_id uuid FK NULL` (set only on a recipient session), `version bigint`; partial `UNIQUE(challenge_id) WHERE challenge_id IS NOT NULL` (one recipient session per challenge); partial `UNIQUE(player_id) WHERE status IN ('created','active')` (one open session per player; creating a new session moves the old open one to `expired` in the same transaction); reward exactly once via ledger key `game:{sessionId}`. A session with `challenge_id` set is not eligible as a `creator_session_id`. `seed` is stored at creation but sent to the client only in the start response. Store compressed event trace separately with short retention.

**game_input_traces**: `session_id uuid PK/FK`, `compressed_trace bytea`, `encoding text`, `event_count int`, `received_at`; size cap and retention policy.

**challenges**: `id uuid PK`, `creator_player_id uuid FK`, `creator_session_id uuid FK UNIQUE` (one challenge per verified run), `recipient_player_id uuid FK NULL` (set on accept; preset for a rematch or a profile challenge), `invite_token_hash bytea UNIQUE` (token = base64url(HMAC-SHA256(server secret, challenge id)); only the hash is stored, and an idempotent replay rebuilds the share URL without storing the raw token), `ruleset_id text FK`, `status enum(open,accepted,completed,expired,cancelled)`, `created_at`, `accept_deadline`, `accepted_at NULL`, `complete_deadline NULL`, `recipient_session_id uuid FK UNIQUE NULL` (set by compare-and-swap when the recipient creates the session), `winner_player_id uuid NULL` (no FK: its check would lock the other player's row and deadlock crossed challenges; a CHECK limits it to the two participants; NULL on a `completed` challenge = tie), `rematch_of_challenge_id uuid FK NULL`, `suppressed bool NOT NULL DEFAULT false` (created against a player who blocked the creator; never shown to that recipient; expires normally), `resolved_at NULL`, `version bigint`; `CHECK(recipient_player_id IS NULL OR recipient_player_id <> creator_player_id)`; partial `UNIQUE(rematch_of_challenge_id, creator_player_id) WHERE rematch_of_challenge_id IS NOT NULL` (one rematch per participant per challenge); indexes `(creator_player_id,created_at DESC)`, `(recipient_player_id,status)`, `(status,accept_deadline)`.

**quest_definitions**: `id uuid PK`, `code text`, `version int`, `period enum(daily,once)`, `criteria jsonb`, `target int NOT NULL CHECK(target > 0)`, `reward_coins bigint`, `reward_xp int`, `is_active bool`.

**player_quests**: `id uuid PK`, `player_id uuid FK`, `quest_id uuid FK`, `period_start date`, `target int NOT NULL` (copied from `quest_definitions.target` at row creation), `progress int NOT NULL DEFAULT 0 CHECK(progress BETWEEN 0 AND target)`, `status enum(active,completed,claimed)`, `completed_at NULL`, `claimed_at NULL`, `UNIQUE(player_id,quest_id,period_start)`.

**player_daily_counters**: `player_id uuid FK`, `day date` (UTC), `runs_rewarded int NOT NULL DEFAULT 0`, `xp_from_games int NOT NULL DEFAULT 0`, `challenge_bonuses int NOT NULL DEFAULT 0`, `PRIMARY KEY(player_id,day)`. Updated only by atomic `INSERT … ON CONFLICT DO UPDATE … RETURNING` in the same transaction as the reward it limits (§7).

**achievements**: `id uuid PK`, `code text UNIQUE`, `criteria jsonb`, `reward_item_id uuid NULL`; **player_achievements**: `(player_id,achievement_id) PK`, `unlocked_at`.

**friendships** (P1): `requester_id`, `addressee_id`, `status enum(pending,accepted,declined)`, timestamps; canonical pair unique and no self-relations. MVP may launch with challenge links before full friendship UI. Blocking is not a friendship state; it lives in `player_blocks`.

**player_blocks** (MVP): `blocker_player_id uuid FK`, `blocked_player_id uuid FK`, `created_at`, `PRIMARY KEY(blocker_player_id,blocked_player_id)`, `CHECK(blocker_player_id <> blocked_player_id)`; index `(blocked_player_id)` for reverse lookup. A block in either direction prevents challenges between the pair. Private: the blocked player is not told.

**player_reports**: `id uuid PK`, `reporter_player_id uuid FK`, `target_player_id uuid FK`, `reason enum(offensive_name,cheating,harassment,other)`, `details varchar(500) NULL`, `status enum(open,actioned,dismissed)`, `created_at`, `reviewed_at NULL`, `reviewed_by text NULL`; restricted access.

**admin_audit_logs**: `id uuid PK`, `actor_subject text NOT NULL`, `actor_role enum(moderator,administrator) NOT NULL`, `action text NOT NULL`, `entity_type text NOT NULL`, `entity_id text NOT NULL`, `before_json jsonb NULL`, `after_json jsonb NULL`, `reason text NOT NULL`, `created_at timestamptz NOT NULL`; index `(entity_type,entity_id,created_at DESC)`, `(actor_subject,created_at DESC)`; append-only (trigger) and restricted.

**data_export_requests**: `id uuid PK`, `player_id uuid FK`, `status enum(queued,running,ready,failed)`, `storage_key text NULL`, `download_expires_at timestamptz NULL`, `failure_code text NULL`, `created_at`, `completed_at NULL`; index `(player_id,created_at DESC)`. At most one `queued|running` request per player (partial UNIQUE).

**deletion_requests**: `id uuid PK`, `player_id uuid` (no FK: the row outlives the player), `status enum(queued,processing,completed)`, `created_at`, `completed_at NULL`. Retained per §11 retention policy.

**outbox_jobs**: `id uuid PK`, `kind text NOT NULL`, `job_key text UNIQUE NOT NULL`, `payload jsonb NOT NULL`, `status enum(pending,done,failed)`, `attempts int NOT NULL DEFAULT 0`, `run_after timestamptz NOT NULL`, `last_error text NULL`, `created_at`; index `(status,run_after)`. Written in the same transaction as the event that needs follow-up work; workers claim rows with `FOR UPDATE SKIP LOCKED`; each job is idempotent by `job_key`.

**idempotency_records**: `actor_subject text` (player id, or admin auth subject), `operation text`, `key uuid`, `request_hash text`, `status enum(in_progress,completed)`, `locked_until timestamptz NULL`, `response_status int NULL`, `response_body jsonb NULL`, `created_at`, `expires_at` (created + 72 h), `PRIMARY KEY(actor_subject,operation,key)`. `Idempotency-Key` must be a UUID (422 `invalid_idempotency_key` otherwise). Protocol:
1. In a short transaction: `INSERT … (status = in_progress, locked_until = now() + 30 s) ON CONFLICT DO NOTHING`.
2. If a row already exists: different `request_hash` → 422 `idempotency_key_mismatch`; `completed` → return the stored response; `in_progress` with `locked_until > now()` → 409 `request_in_progress`; `in_progress` with an elapsed lock → take it over with `UPDATE … SET locked_until = now() + 30 s WHERE status = 'in_progress' AND locked_until < now() RETURNING` (if no row returns, answer 409).
3. Execute the operation and, **in the same transaction**, write `response_status`, `response_body`, `status = completed` and `locked_until = NULL` as a compare-and-swap on the caller's own lease (`WHERE status = 'in_progress' AND locked_until = :lease`; no row → another request took over, so roll back). `CHECK((status = 'in_progress') = (locked_until IS NOT NULL))`. Exception: `createChallenge` and `createRematch` store the body without `shareUrl` and rebuild it on replay from the challenge id (the invite token is HMAC-derived), so no raw token is ever stored. Business outcomes (2xx and 4xx such as `insufficient_funds`) are stored and replayed. A 5xx or a crash stores nothing; the lock elapses and a retry takes over.
Clients must not reuse a key after 24 h. The ledger `UNIQUE(player_id,ledger_key)` remains the final guard if a record expired.

### Database invariants
1. Every wallet debit and corresponding inventory grant commit in one DB transaction.
2. Wallet row locked (`SELECT ... FOR UPDATE`) for debits/credits; `CHECK(balance>=0)` as defense in depth.
3. Rewards use the server-derived `ledger_key` (unique per player), never a client-supplied amount or raw client key.
4. Equipping requires owned, active/usable and compatible item; one item per slot, and one per accessory attachment point (max 2).
5. Challenge transitions use compare-and-swap version/status predicate to avoid double acceptance. `version`/`revision` columns are incremented by a trigger on every UPDATE.
6. Game sessions and rulesets are immutable after verification; audit corrections use compensating entries.
7. Retention and cascade policies are explicitly tested before deletion features ship.
8. Inserting a `player_blocks` row and cancelling the pair's `open`/`accepted` challenges commit in one DB transaction, under the pair lock of invariant 12.
9. Challenge winner bonus uses ledger reason `challenge_reward` with `ledger_key = challenge:{challengeId}`; at most one per challenge (partial unique index on `reference_id` where `reason = 'challenge_reward'`).
10. Run verification, daily counter update, reward ledger rows, XP/level update and the run's own quest progress commit in one DB transaction that locks rows of **one player only** (player rows with `FOR NO KEY UPDATE`, never `FOR UPDATE`, so foreign-key checks from other transactions do not wait). Work for the other participant of a challenge (winner bonus, challenge quest progress) runs in `outbox_jobs` (`job_key = challenge-resolved:{challengeId}:{playerId}`), one transaction per player. No transaction locks rows of two players, so crossed challenges cannot deadlock.
11. Quest progress uses one atomic statement; never read-modify-write:
    ```sql
    INSERT INTO player_quests (id, player_id, quest_id, period_start, target, progress, status, completed_at)
    VALUES (gen_random_uuid(), :player, :quest, :day, :target, LEAST(:target, :delta),
            CASE WHEN :delta >= :target THEN 'completed' ELSE 'active' END,
            CASE WHEN :delta >= :target THEN now() END)
    ON CONFLICT (player_id, quest_id, period_start) DO UPDATE SET
      progress     = LEAST(player_quests.target, player_quests.progress + :delta),
      status       = CASE WHEN player_quests.status = 'active'
                           AND player_quests.progress + :delta >= player_quests.target
                          THEN 'completed' ELSE player_quests.status END,
      completed_at = CASE WHEN player_quests.status = 'active'
                           AND player_quests.progress + :delta >= player_quests.target
                          THEN now() ELSE player_quests.completed_at END;
    ```
12. Pair lock: block, challenge creation with a recipient, token accept, rematch and recipient-session creation first take `pg_advisory_xact_lock(hashtextextended(min(a,b)::text || ':' || max(a,b)::text, 0))` for the two player ids, then re-check `player_blocks` inside the lock.

## 5. API contracts v1

Base path `/api/v1`. The machine-readable contract is `packages/contracts/openapi/vybe-api.v1.yaml` (decisions: `docs/adr/0001-openapi-contract.md`). JSON UTF-8; UTC ISO 8601; UUID strings; `Idempotency-Key` required for state-changing economic actions. Auth: validated Supabase bearer token for registered users or signed guest cookie for guests. `traceparent` propagated. Errors: RFC 9457 ProblemDetails with `type`, `title`, `status`, `detail`, `instance`, `code`, `traceId`. Never leak internal exception details. `code` is a stable machine-readable string; each endpoint below lists its codes, and the OpenAPI document enumerates them.

**Common codes (all endpoints unless an endpoint states otherwise):** 400 `malformed_request` (unparseable JSON or wrong JSON types), 422 `invalid_idempotency_key` (not a UUID), 401 `unauthenticated`, 404 `player_not_found` (valid Supabase token whose identity has no player yet: the client creates a guest without the bearer token, then links it), 403 `forbidden`, 403 `csrf_check_failed` (cookie-authenticated unsafe request without `X-Requested-With: vybe-web`), 403 `account_suspended` (economic actions and challenge creation), 404 `not_found`, 409 `revision_conflict`, 409 `request_in_progress`, 413 `payload_too_large`, 422 `validation_failed` (field errors in `errors[]`), 422 `idempotency_key_mismatch`, 428 `idempotency_key_required`, 429 `rate_limited`, 500 `internal_error`.

### Identity / avatar
- `POST /guests` → `201 {playerId,handle,guestExpiresAt}` + secure guest cookie; rate limited; requires `X-Requested-With: vybe-web` (403 `csrf_check_failed`), so a cross-site form cannot replace a guest cookie; 409 `already_authenticated` when a valid guest cookie or bearer token is present.
- `POST /accounts/link` `{linkProof}` → `200 {playerId,accountLinked:true}`; proof must be verified server-side and must be a non-anonymous Supabase identity with a confirmed email; the response clears the guest cookie; 409 `identity_already_linked`; 422 `invalid_link_proof` (the guest session stays valid; 401 means the guest session itself is gone).
- `GET /me` → `200 {id,handle,kind,status,suspendedUntil,level,xpTotal,walletBalance,avatarRevision}`; `status` ∈ `active`, `suspended`, `deleting`; `kind` (`guest` or `registered`) drives the link-account prompt.
- `GET /me/avatar` → `200 {revision,bodyPresetId,skinToneId,facePresetId,hairPresetId,hairColorId,equipped}`; `equipped` uses the `equipped_json` shape (§4).
- `PUT /me/avatar` `{bodyPresetId,skinToneId,facePresetId,hairPresetId,hairColorId,expectedRevision}` → `200 {revision,avatar}`; 409 `revision_conflict`; 422 `invalid_preset`; 409 `incompatible_equipment` with `itemIds[]` when a body preset change leaves equipped items incompatible (nothing changes; the client unequips them first).
- `GET /profiles/{handle}` → public fields only; 404 `not_found` if unavailable; `Cache-Control: private, no-store` (optional auth).

### Safety
- `POST /me/blocks` `{handle}` → `204`; idempotent; cancels pending challenges between the pair (invariant 8); 404 `not_found`; 422 `self_block`.
- `DELETE /me/blocks/{handle}` → `204`; idempotent; does not restore cancelled challenges.
- `GET /me/blocks?cursor=&limit=` → players the caller blocked; never exposes who blocked the caller.
- `POST /reports` `{targetHandle,reason,details?}` → `202 {reportId}`; `reason` from `player_reports.reason`; 404 `not_found`; 422 `self_report`; rate limited (provisional: 10/day per reporter).

### Catalog / inventory / economy
- `GET /catalog/items?slot=top&cursor=...&limit=24` → `{items:[{id,sku,name,price,slot,rarity,minLevel,previewUrl,owned}],nextCursor}`; only active items with `price > 0` in their availability window; server-generated asset URLs; `Cache-Control: private, no-store` (`owned` depends on the caller).
- `GET /me/inventory` → owned item IDs and metadata, including `source`.
- `POST /purchases` `{itemId}` + Idempotency-Key → `200 {transactionId,itemId,balanceAfter}`; 409 `insufficient_funds`; 409 `already_owned`; 403 `level_too_low`; 422 `item_unavailable` (inactive, outside window or unknown); 422 `item_not_purchasable` (`price = 0`).
- `POST /me/equipment` `{slot,itemId|null,attachIndex?,expectedAvatarRevision}` → `200 {revision,equipped}`; `attachIndex` (0 or 1) is required for `slot = accessory` and forbidden otherwise; 422 `item_already_equipped` when the same accessory is already at the other attachment point; 403 `item_not_owned`; 422 `incompatible_item` (slot or body preset); 422 `item_inactive`; 409 `revision_conflict`.
- `GET /me/outfits` → `200 {outfits:[{id,name,equipped,valid,invalidItemIds[]}]}`.
- `POST /me/outfits` `{name}` → `201 {id}`; saves the current equipment; 409 `outfit_limit_reached` (max 3).
- `PUT /me/outfits/{id}` `{name}` → `200`; `DELETE /me/outfits/{id}` → `204`.
- `POST /me/outfits/{id}/apply` `{expectedAvatarRevision}` → `200 {revision,equipped}`; all-or-nothing; 422 `outfit_items_invalid` with `itemIds[]` (no longer owned, active or compatible); 409 `revision_conflict`.
- `GET /me/wallet/transactions?cursor=&limit=` → paginated read-only ledger.

### Gameplay
- `GET /games/rulesets/{id}` → `200 {id,gameId,version,config,checksum}`; immutable, `Cache-Control: public, max-age=31536000, immutable`. The client verifies `checksum` before it uses `config`.
- `POST /games/street-hoops/sessions` `{challengeId?:uuid}` → `201 {sessionId,status:"created",rulesetId,rulesetChecksum,startDeadline}`. No seed yet, so a player cannot inspect seeds before committing to a run. Any other `created`/`active` session of the caller becomes `expired` (one open session per player). Without `challengeId`: the current active ruleset and a fresh seed. With `challengeId`: the session uses the creator run's `seed` and `rulesetId`; only the bound recipient of an `accepted` challenge may call it, before `complete_deadline`; the server sets `challenges.recipient_session_id` by compare-and-swap. Codes: 404 `challenge_unavailable` (unknown, or caller is not the recipient, or a block exists); 409 `challenge_session_exists`; 410 `challenge_expired`.
- `POST /games/sessions/{id}/start` → `200 {seed,startedAt,submitDeadline}`; moves `created -> active`; the client calls it after assets are loaded and the countdown ends, and sets its local time 0 when the response arrives. `seed` is a JSON integer in uint32 range. Idempotent (a repeat returns the same values). 410 `session_expired` after `startDeadline` or if the session was replaced; for a challenge session, 410 `challenge_expired` when `now >= complete_deadline` and 404 `challenge_unavailable` when the challenge is no longer `accepted`.
- `POST /games/sessions/{id}/submit` `{rulesetId,events,clientReportedScore}` + Idempotency-Key. Each event is one shot `{tMs,aimX,power}`. **Exception to the common 400:** OpenAPI models `rulesetId` as `string` and `events` as an array of free-form values (`items: {}`), so the framework never rejects event content; the server validates it. Responses (`200` unless noted):
  - `202 {sessionId,status:"submitted"}` (asynchronous verification; poll `GET`).
  - `{status:"verified",score,rewards:{coins,xp,levelAfter}}`.
  - `{status:"rejected",rejectionReason}`; terminal. `rejectionReason` ∈ `not_started`, `ruleset_mismatch`, `invalid_event`, `out_of_range`, `too_many_events`, `non_monotonic`, `cadence_violation`, `submitted_too_early`. Check order: §7.
  - `{status:"expired"}`; terminal. Returned when the session is already `expired` or `submitted_at > submit_deadline` (the late submit moves the session to `expired`, the same state the sweeper writes).
  - **`invalid_event`:** an event is not a JSON object, a field is missing or extra, or a value is not a strict JSON integer. A strict integer is a JSON number token matching `-?(0|[1-9][0-9]*)` (no fraction, no exponent) with absolute value below 2^31. `820.0`, `1e3`, `"120"`, `null`, `true` and `2147483648` are all `invalid_event`. This check runs on the raw JSON tokens in the API only; the shared simulator (C# and TS) receives events that already passed it.
  - 400 only for unparseable JSON or a body that is not an object with `rulesetId` (string) and `events` (array); 413 for a body above the size cap (16 KB). In both cases the session stays `active` until `submit_deadline`.
  - Client score is diagnostic only; server recomputes.
- `GET /games/sessions/{id}` → state, `rejectionReason` and verified score/rewards; no trace exposed to others.

### Challenges
- `POST /challenges` `{creatorSessionId,recipientHandle?}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`. The creator session must be the caller's, `verified`, not a recipient session, have at least `config.minShotsForRewards` events, and not be used for another challenge. `recipientHandle` presets the recipient (profile challenge). Codes: 422 `session_not_eligible`; 409 `session_already_used`; 404 `not_found` for an unknown handle; 422 `self_challenge`; 422 `recipient_blocked` when the **caller** blocked the recipient (the caller already knows this). When the recipient blocked the caller, the API returns `201` and stores the challenge with `suppressed = true`: the recipient never sees it and it expires normally, so the caller cannot detect the block.

**Visible status rule (block privacy):** the API never returns `cancelled`. Internally `cancelled` (always caused by a block in MVP) is shown as `expired` to both participants, in every challenge endpoint and filter.

- `GET /invites/{token}` → safe public challenge preview, no secret token hash; 404 `challenge_unavailable` under the same rules as accept; `Cache-Control: private, no-store`.
- `POST /invites/{token}/accept` + Idempotency-Key → `200 {challengeId,status,completeDeadline}`; atomic token redemption under the pair lock (invariant 12). Codes: 404 `challenge_unavailable` (unknown token, preset recipient is someone else, `suppressed`, or a block exists in either direction); 403 `self_accept`; 409 `challenge_already_accepted`; 410 `challenge_expired` (also for internal `cancelled`). The token, suppression and block checks run before all status checks.
- `GET /challenges/{id}` → participants and visible status, filtered by privacy rules; includes `shareUrl` only for the designated recipient (rematch or profile challenge) while `open`, so they can accept in the app.
- `GET /me/challenges?role=created|received&status=&cursor=&limit=` → caller's challenges with visible status, newest first; `suppressed` challenges are omitted for the recipient.
- `POST /challenges/{id}/rematch` `{creatorSessionId}` + Idempotency-Key → `201 {challengeId,shareUrl,acceptDeadline}`. Original challenge must be `completed`; caller must be one of its participants and becomes the new creator; the other participant becomes the preset `recipient_player_id`; `creatorSessionId` follows the same rules as `POST /challenges`; cannot recycle prior result; one rematch per participant per challenge. Codes: as `POST /challenges` (incl. the `suppressed` rule); 409 `challenge_not_completed`; 409 `rematch_already_created`.

### Quests and account
- `GET /me/quests` → `200 {quests:[{playerQuestId,code,periodStart,progress,target,status,rewardCoins,rewardXp,claimableUntil}]}`; first creates today's rows for all active daily quests with `INSERT … ON CONFLICT DO NOTHING` (progress 0), so every quest has a `playerQuestId`; includes completed, unclaimed quests from earlier periods while claimable.
- `POST /me/quests/{playerQuestId}/claim` + Idempotency-Key → `200 {coinsGranted,xpGranted,level}`. A completed quest stays claimable for 7 days after `period_start` (provisional). Codes: 409 `quest_not_completed`; 409 `quest_already_claimed`; 410 `quest_claim_expired`.
- `POST /me/data-export` → `202 {exportId}`; 409 `export_in_progress` (with its `exportId`) while one is `queued` or `running`; `GET /me/data-export/{exportId}` → `{status:queued|running|ready|failed, downloadUrl?, downloadExpiresAt?}`; `downloadUrl` is short-lived and single-player.
- `POST /me/deletion-requests` + Idempotency-Key → `202 {requestId}` where `requestId` = the key (if the response is lost after the sessions are revoked, the client still knows the id), requires reauthentication for registered account: newest `amr[].timestamp` of the Supabase token at most 5 minutes old (provisional; not `iat`, which a refresh renews), else 403 `reauthentication_required`; `GET /deletion-requests/{requestId}` (no authentication: the sessions are revoked, so the random `requestId` is the capability; no personal data in the response) → `{status:queued|processing|completed,completedAt?}`.

### Admin (base `/api/v1/admin`; moderator or administrator claim)
Every write requires `reason` and writes `admin_audit_logs` in the same transaction.
- `GET /reports?status=open&cursor=` (moderator); `POST /reports/{id}/resolve` `{status:actioned|dismissed,reason}` (moderator).
- `POST /players/{id}/suspension` `{reason,until?}`; `POST /players/{id}/suspension/lift` `{reason}` (moderator).
- `PATCH /catalog/items/{id}` `{isActive,reason}` (administrator).
- `POST /wallet-adjustments` `{playerId,delta,reason}` + Idempotency-Key (administrator only; ledger key `admin:{idempotencyKey}`, so a retry never adjusts twice); 409 `insufficient_funds` if the result is negative.
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
`created -> active -> submitted -> verified | rejected`; `created -> rejected` (submit before start: `not_started`); `created -> expired` (not started before `start_deadline`, or replaced by a newer session); `active -> expired` (no submission before `submit_deadline`, a late submit, or replaced by a newer session); terminal states `verified,rejected,expired`. `POST …/start` moves `created -> active` and sets `started_at` (time 0 for `tMs`) and `submit_deadline`. Server clock is authoritative. Time checks at submit (values from `config`):
- `submitted_at > submit_deadline` → `expired` (response `{status:"expired"}`). The sweeper writes the same state, so both writers agree; each uses a status compare-and-swap from `active`.
- `submitted_at < started_at + maxT - earlyToleranceMs` → `rejected` (`submitted_too_early`), where `maxT` = largest `tMs`, or 0 for an empty trace. This stops traces that claim more game time than has passed.

A recipient session that ends `rejected` or `expired` moves its challenge `accepted -> expired` in the same transaction. A **verified** recipient run of any length (an empty trace scores 0) resolves the challenge by compare-and-swap `accepted -> completed`. If the challenge is no longer `accepted` (for example, cancelled by a block during the run), the CAS matches no row: the run keeps its normal rewards and the challenge is unchanged. `submitted` may be processed synchronously or asynchronously, but repeated submissions must return the canonical result. Only verified sessions may produce rewards or challenges.

### Challenge
`open -> accepted -> completed`; `open -> expired|cancelled`; `accepted -> expired|cancelled`; terminal `completed,expired,cancelled`. Acceptance binds one recipient; completion references one verified run under the challenge ruleset. `completed` with `winner_player_id = NULL` is a tie. A block in either direction moves `open|accepted -> cancelled`. `accepted -> expired` also when the recipient run is `rejected` or `expired`. Deadline rule: session creation **and** `start` require `now < complete_deadline`; a run started before `complete_deadline` may finish after it, within its own `submit_deadline`. The sweeper expires an `open` challenge after `accept_deadline`, and an `accepted` challenge after `complete_deadline` only when `recipient_session_id IS NULL` or that session is `created`, `rejected` or `expired`; it never expires a challenge whose recipient session is `active` or `submitted`. All sweeper writes are status compare-and-swaps. Read endpoints apply the same rule to avoid stale state. The visible status of `cancelled` is `expired` (§5).

On `accepted -> completed`, the recipient's verification transaction writes two `outbox_jobs` (`challenge-resolved:{challengeId}:{playerId}`, one per participant). Each job, in its own transaction for that player only: grants the winner bonus if this player won (daily cap via `challenge_bonuses` on the UTC date of `resolved_at`), and adds `challenge_or_streak` progress on the UTC date of `resolved_at`.

### Quest
`active -> completed -> claimed`; `completed` is set when `progress` reaches `target`. Period rollover creates a new record, never resets claimed history. Server derives progress from verified domain events with the atomic upsert of invariant 11; rows are created by that upsert or by `GET /me/quests`. Quest day (`period_start`): `reward_day` of the run for run-based progress; UTC date of `resolved_at` for challenge progress. A completed quest is claimable for 7 days after `period_start` (provisional).

### Purchase
`requested -> validated -> committed` (atomic debit + inventory grant + ledger); validation failures return no state change. Duplicate request key returns stored outcome; a concurrent duplicate returns 409 `request_in_progress`; key reuse with different body returns 422 `idempotency_key_mismatch` (protocol in `idempotency_records`, §4).

### Account
`guest -> registered -> suspended? -> deleting -> deleted`; a guest token cannot be used after linking. Suspension blocks economic actions and challenge creation.

## 7. Street Hoops game ruleset v1

**Objective:** score maximum points in 60 seconds. Input can be mouse, touch or keyboard; the client quantizes it to the integer units below before it records an event.

**Input units (normative, all integers):**
- `aimX` in [-1000, 1000] (1000 = full court half-width).
- `power` in [0, 1000].
- `tMs` in [0, `durationMs` − 1], milliseconds since the start call (`started_at` on the server; arrival of the start response on the client).
- The trace contains **shot events only** (one event = one release). Aim/power samples between shots are not sent. Non-integer values => `invalid_event`; out-of-range values => `out_of_range`. An empty trace is legal and scores 0.

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
- Cadence: `tMs` strictly increasing; gap between consecutive events >= `minGapMs`; at most `maxEvents` events. Reject traces exceeding configured limits. (With v1 values, 93 = the number of shots that fit: 0, 650, …, 59,800.)
- **PRNG:** PCG32 (XSH-RR output, 64-bit LCG state), seeded with the reference `pcg32_srandom(initstate = seed, initseq = streamId)` from pcg-random.org; `seed` and `streamId` are uint32 widened to uint64. `next()` returns an unsigned 32-bit integer (C# `uint`; JS `Number` in [0, 2^32 - 1] after `BigInt` arithmetic). Draw an integer in [lo, hi] as `lo + (next() mod (hi - lo + 1))` with unsigned `mod`; the small modulo bias is accepted. The JS client uses `BigInt` for the 64-bit state.
- **Basket path:** the timeline has `durationMs / segmentMs` (= 15) segments. Before the run, draw `waypointCount` (= 16) waypoints **interleaved**: for `k = 0, 1, …, 15`, draw `x_k` in [`xMin`, `xMax`], then `p_k` in [`pMin`, `pMax`]. The draw sequence is therefore `x_0, p_0, x_1, p_1, …, x_15, p_15` (32 draws). No other draws occur. For time `t`: `k = t div segmentMs`, `f = t mod segmentMs`, `targetX(t) = x_k + trunc((x_{k+1} - x_k) * f / segmentMs)`, `idealPower(t) = p_k + trunc((p_{k+1} - p_k) * f / segmentMs)`. `trunc` rounds toward zero (C# integer `/`; JS `Math.trunc`). Intermediate values stay below 2^31.
- **Shot evaluation:** `dx = |aimX - targetX(tMs)|`, `dp = |power - idealPower(tMs)|`. Perfect when `dx <= perfectDx` and `dp <= perfectDp`; hit when `dx <= hitDx` and `dp <= hitDp`; otherwise miss.
- **Points:** hit = `hitPoints`, perfect = `perfectPoints`, miss = 0. `streak` = number of consecutive hits including the current shot (a perfect is a hit). Streak bonus = `min(streakMax, streakStep * (streak - streakStart + 1))` when `streak >= streakStart`, else 0. With v1 values this is `min(100, 25 * (streak - 2))`: the bonus grows +25, +50, +75, then stays at +100. A miss resets `streak` to 0. Maximum score = 22,900 (93 consecutive perfects).
- **Simulator contract (identical in C# and TS):** `evaluate(input) -> result`.
  - `input = {config, rulesetId, seed, session:{status, startedAtMs, submitDeadlineMs}, submission:{rulesetId, submittedAtMs, events:[{tMs,aimX,power}]}}`. `session.status` ∈ `created`, `active`, `expired`. Times are integer milliseconds on one server clock (epoch ms in production; small values in fixtures). Events have already passed the API `invalid_event` check.
  - `result` is one of `{outcome:"expired"}`, `{outcome:"rejected", reason}`, or `{outcome:"verified", score, eventCount, maxStreak, shots:[{tMs,targetX,idealPower,dx,dp,result,points,streak}]}` with `result` ∈ `perfect`, `hit`, `miss`.
- **Check order.** Each check examines the **whole trace** before the next check runs (category order, not event order). The first failing check decides the outcome:
  1. `session.status = created` → rejected `not_started`.
  2. `session.status = expired` or `submittedAtMs > submitDeadlineMs` → `expired`.
  3. `submission.rulesetId ≠ rulesetId` → rejected `ruleset_mismatch`.
  4. (API only, before the simulator) → rejected `invalid_event`.
  5. Any field outside its range → rejected `out_of_range`.
  6. More than `maxEvents` events → rejected `too_many_events`.
  7. Any `tMs[i] <= tMs[i-1]` → rejected `non_monotonic`.
  8. Any `tMs[i] - tMs[i-1] < minGapMs` → rejected `cadence_violation`.
  9. `submittedAtMs < startedAtMs + maxT - earlyToleranceMs` (`maxT` = largest `tMs`, 0 if empty) → rejected `submitted_too_early`.
  
  Then the simulator computes the canonical score. A rejected or expired run gets no reward.
- **Golden fixtures** in `packages/game-rules/fixtures/`:
  - `pcg32.json`: reference vectors (incl. the official pcg32-demo vector for `initstate = 42, initseq = 54`).
  - `rulesets/street-hoops-v1.json`: `{id, config, checksum}`.
  - `scoring/*.json`: `{name, description, waypoints, input, expected}`, where `input` and `expected` follow the simulator contract (`input` carries `config`, `rulesetId` and `seed`).
  - `pcg32.json` and all scoring/rejection fixtures are generated by `pnpm --filter @vybe/game-rules generate:fixtures` from the TS reference; hand-checked unit tests in both languages guard the reference itself.
  - `rejections/*.json`: same shape, one or more fixtures per outcome and reason of steps 1–3 and 5–9.
  
  The C# server and the TS client must both pass every fixture in CI. Raw-token `invalid_event` cases (`820.0`, `1e3`, `"120"`, `null`, `2147483648`) are API test cases, not simulator fixtures.
- Client animations may use smooth Three.js physics for appearance, but the shown outcome must match the deterministic result. Prototype the feel before freezing numeric thresholds.

**Anti-automation (limit of replay validation):** the client gets the `seed` at start and must render the basket, so a bot can compute a perfect trace. The seed arrives only after the player commits to the run, and only one session can be open, so a player cannot inspect seeds and keep only easy ones. Replay proves a trace is legal, not that a human played. MVP records signals in `game_sessions.review_flags` and takes **no automatic action**: perfect-shot rate above threshold in a run; low variance of inter-shot gaps; low variance of `dx`/`dp`; run volume at the daily cap over many consecutive days. Flagged runs stay verified and rewarded; flags feed the admin review queue. Thresholds are set from prototype and beta telemetry.

**Rewards per verified run:** base `coins = min(150, 20 + floor(score/500)*10)` and `xp = min(80, 10 + floor(score/400)*5)`. All rewards computed on server in the verification transaction (invariant 10); no reward for rejected runs. These are tuning defaults, not measured economy balance.
- **Reward day:** `reward_day` = UTC date of `started_at`, so late asynchronous verification after 00:00 UTC does not move a run to the next day. Daily reset UTC, explicitly shown in UI.
- **Minimum play:** a run is **reward-eligible** when it is verified and has at least `minShotsForRewards` events, whatever its coin tier. A verified run below that grants 0 coins and 0 XP, makes no run-based quest progress, does not count in `runs_rewarded`, and cannot create a challenge. As a recipient run it still resolves its challenge (§6).
- **Run cap:** increment `player_daily_counters.runs_rewarded` for (`player_id`, `reward_day`) with `INSERT … ON CONFLICT DO UPDATE … RETURNING`; the returned value `n` is this run's number. `n <= 10` → full coins; `11 <= n <= 20` → `floor(coins / 4)`; `n > 20` → 0 coins. Concurrent runs get distinct `n`.
- **XP cap:** `grant = min(xp, 800 - xp_from_games)` using the locked counter row, so the last run fills exactly to 800 (example: 790 + 80 grants 10). Then `xp_from_games += grant`.
- A ledger row is written only when coins > 0 (`CHECK(delta<>0)`).

**Challenge rules:** creator run's immutable ruleset and seed define recipient's conditions; recipient uses separate session. Winner has higher canonical score; ties favor creator for display only and store `winner_player_id = NULL`. Winner receives +50 V-Coins (ledger reason `challenge_reward`) once per challenge, capped at 5 winner bonuses/day via `player_daily_counters.challenge_bonuses` for the UTC date of `resolved_at` (same atomic upsert pattern), granted by the `challenge-resolved` outbox job (§6); standard gameplay rewards remain separate. No stake, no currency transfer between players.

## 8. XP, quests, catalog and economy

**XP thresholds:** Level 1 starts at 0; total XP required to reach level `L` (L>=1) = `100*(L-1)^2`. Level 50 at 240,100 XP; level 50 is the cap (XP keeps accumulating, level stays 50). Persist `xp_total`; the domain service derives the level and writes the cached `players.level` in the same transaction; no client authority. Level-up grants 100 V-Coins once per new level via ledger source `level_reward` with `ledger_key = level:{L}`. One XP grant that crosses several levels grants one reward per level. Quest XP is not limited by the 800/day gameplay cap.

**Daily quests (UTC):**
1. `play_3`: finish 3 reward-eligible verified runs (`target` 3) → 100 coins + 60 XP.
2. `score_1500`: achieve >=1,500 points in one reward-eligible run (`target` 1) → 100 coins + 60 XP.
3. `challenge_or_streak`: take part in one challenge that reaches `completed` (creator or recipient; counted on the UTC date of `resolved_at`) **or** reach `maxStreak >= 5` in one reward-eligible run (`target` 1) → 100 coins + 60 XP. The solo path keeps the quest open to players without friends; quests never require inviting new people.

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
- Auth: validate JWT issuer/audience/signature and token expiry; guest cookie with CSRF protection on cookie-authenticated mutations (required `X-Requested-With: vybe-web` header, allowed by CORS only from the VYBE web origin); secure CORS and origin checks.
- Authorization: resource owner policies on every endpoint; separate moderator/admin claims; never trust client `playerId`.
- API: per-IP and per-player rate limits; payload/event count/size caps; structured validation; request timeouts; parameterized SQL; no arbitrary uploaded 3D files in MVP.
- Invite URLs: 256-bit opaque token derived as base64url(HMAC-SHA256(server secret, challenge id)), only hashed in DB (never in idempotency records), single-use, expiration; avoid leaking via analytics/referrers, apply `Referrer-Policy: no-referrer` on token pages.
- Browser: CSP, safe HTML escaping, dependency scans, no tokens in localStorage, secure cookies.
- GDPR: data inventory, legal bases, retention, privacy notice, export/deletion workflow, processor agreements, breach process. Determine minors policy before launch; no unrestricted chat.
- Abuse: username filtering, report/block, moderation review, audit logs, appeal channel.
- Data retention defaults for review: raw game traces 30 days, operational logs 30 days, anti-fraud audit 90 days (audit-log deletes only by the retention job, ADR 0002), account data until deletion request; final policy requires legal/privacy approval.

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
- Block racing with challenge create/accept → no `open`/`accepted` challenge survives between the pair; challenge to a player who blocked the creator → `suppressed`, invisible to the recipient; crossed challenges A→B and B→A verified concurrently → no deadlock, both resolve; stale `in_progress` idempotency row → takeover after `locked_until`; late submit vs sweeper → one `expired` state; empty recipient trace → challenge `completed`; second session → previous one `expired`; quest row created by `GET /me/quests` then completed by upsert → claimable.
- API raw-token cases for `invalid_event`: `820.0`, `1e3`, `"120"`, `null`, `true`, `2147483648`, missing field, extra field.

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

## 17. Decisions requiring sign-off

Product sign-off on 2026-11-07 (record and reasons: `docs/decisions/`). v1.4 (§18) applies them. Open items stay listed.

| # | Decision | Signed option | Still open |
|---|---|---|---|
| 1 | Age eligibility, minors, countries | Adults only (18+ or local age of majority), neutral age gate before guest creation | Country list and gate wording (counsel, DPO) |
| 2 | Guest lifetime, recovery | Sliding 30 days of inactivity, cap 90 days; no support transfer | — |
| 3 | 3D art direction, licensing, ownership | Contractor or studio under work-for-hire; one in-house pipeline owner | Contract (counsel); final style guide (art lead) |
| 4 | Street Hoops tuning, thresholds | v1 gameplay values and deadlines kept; full-length run and score floor for rewards; `review_flags` starting thresholds | Re-tune after 2 weeks of beta data |
| 5 | Economy balance | Earn formulas kept; catalog repriced and grown (≥ 30 items at beta) | Review after 2 weeks of beta data |
| 6 | Profile visibility | Signed-in players only by default; "private" toggle | Counsel review |
| 7 | Hosting, concurrency, on-call | Supabase Pro + one always-on API container on Fly.io, same region, same-origin `/api`; USD 150/month beta ceiling; named primary and backup on-call | Region; on-call names |
| 8 | Support, retention, deletion wording | Pseudonymized ledger kept (if counsel accepts); retention draft; email support (3 business days) | Final notice and terms (counsel, DPO) |

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

### v1.3 (2026-11-06) — v1.2 review findings closed
1. **§5 submit validation:** endpoint-specific exception to the common 400; `events` modeled as free-form in OpenAPI; strict JSON integer defined (`-?(0|[1-9][0-9]*)`, below 2^31); raw-token check is API-only.
2. **§7 check order:** one authoritative list in §7, applied category by category over the whole trace; simulator contract `evaluate(input) -> result` with session timing in the input; fixture layout defined.
3. **§6/§7 empty trace:** `maxT = 0` for an empty trace; an empty trace is legal and scores 0; a verified recipient run of any length resolves the challenge.
4. **§6 session states:** `created -> rejected` (`not_started`); a late submit gives `expired` (same as the sweeper); `submitted_too_late` removed; `{status:"expired"}` response.
5. **§2/§3/§5 block privacy:** API never shows `cancelled` (shown as `expired`); 410 `challenge_cancelled` removed; by-token preview follows accept rules; challenge to a player who blocked the creator is stored `suppressed`; 422 `recipient_blocked` only when the caller is the blocker.
6. **§4 pair lock:** invariant 12, advisory lock on the ordered player pair for block, create, accept, rematch and recipient-session creation.
7. **§4 idempotency:** `locked_until` lease with takeover; response stored in the same transaction as the operation; 4xx outcomes replayed; keyed by `actor_subject`; UUID keys; admin ledger key `admin:{idempotencyKey}`; purchase ledger key `purchase:{itemId}`.
8. **§5/§6 challenge deadline:** `start` also checks `complete_deadline`; sweeper never expires a challenge with an `active`/`submitted` recipient session; verified run on a non-`accepted` challenge keeps rewards only.
9. **§4/§6 deadlock avoidance:** each transaction locks one player's rows; winner bonus and challenge quest progress move to per-player `outbox_jobs`.
10. **§4/§5/§6/§8 quests:** `player_quests.target` copied from the definition; full upsert SQL sets `status`/`completed_at`; `GET /me/quests` creates the day's rows; quest day defined; "reward-eligible" defined.
11. **§4 missing storage:** `data_export_requests`, `deletion_requests`, `outbox_jobs`; `players.suspended_until` and `suspension_reason`; full types and PKs for `admin_audit_logs` and `idempotency_records`.
12. **§4/§5/§7 seed exposure:** seed returned by `start`, not at creation; one open session per player (partial UNIQUE; a new session expires the old one).
13. **§7 units:** `tMs` range uses `durationMs`; 93 shown only as the v1 value of `maxEvents`.
14. **§4/§5 error codes:** 422 `self_challenge`, 422 `item_already_equipped`, 409 `rematch_already_created` with partial UNIQUE, 422 `invalid_idempotency_key`.

### v1.3.1 (2026-11-06) — alignment with the OpenAPI contract (ADR 0001)
1. **§5 invite routes:** `/challenges/by-token/{token}` → `/invites/{token}` (the old path was ambiguous with `/challenges/{challengeId}/…`).
2. **§5 admin:** `DELETE …/suspension` with a body → `POST …/suspension/lift`.
3. **§5/§11 CSRF:** `X-Requested-With: vybe-web` header for cookie-authenticated unsafe requests; 403 `csrf_check_failed`.
4. **§5 codes:** 409 `export_in_progress`; 500 `internal_error`.
5. **§5 `GET /me`:** adds `kind`.

### v1.3.2 (2026-11-06) — OpenAPI contract review
1. **§5 deletion:** reauthentication uses `amr[].timestamp`, not `iat`; status endpoint moved to `GET /deletion-requests/{requestId}` without authentication (capability id).
2. **§4 invite token:** derived with HMAC from the challenge id, so idempotent replays never store the raw token.
3. **§5 `POST /guests`:** CSRF header required; 409 `already_authenticated`.
4. **§2 block privacy:** residual inference risk accepted and documented.
5. **§5 `POST /accounts/link`:** 422 `invalid_link_proof`.
6. **§5 `GET /me`:** adds `status` and `suspendedUntil`.
7. **§4 handles:** lowercase storage and output; case-insensitive input.

### v1.3.3 (2026-11-06) — second OpenAPI contract review
1. **§4/§11 invite token:** idempotency records store challenge responses without `shareUrl`; token text updated to HMAC-derived.
2. **§5 CSRF:** the header is declared on all unsafe guest-callable operations; the server checks it itself (403 `csrf_check_failed`).
3. **§5 `GET /challenges/{id}`:** `shareUrl` for the designated recipient of an `open` rematch or profile challenge.
4. **§4/§5 linking:** anonymous or unconfirmed Supabase identities rejected; guest cookie revoked and cleared ("rotate" removed).
5. **§5 deletion/export:** `requestId` = Idempotency-Key; 409 `export_in_progress` returns `exportId`.
6. **§5 common codes:** 404 `player_not_found` for a Supabase identity without a player.
7. **§5 caching:** `Cache-Control: private, no-store` on caller-dependent public responses.

### v1.3.4 (2026-11-06) — database schema (ADR 0002)
1. **§4 storage:** schema `vybe`; UUIDv7 keys; enum columns as `text` + CHECK.
2. **§4 `saved_outfits`:** `slot` column (1–3) with `UNIQUE(player_id,slot)` enforces the 3-outfit limit.
3. **§4 `catalog_items`:** at least one compatible body preset (§9).
4. **§4 append-only / immutable:** enforced by triggers on `wallet_transactions`, `admin_audit_logs`, `game_rulesets` and finished `game_sessions`; audit-log retention deletes need `vybe.retention_delete`.
5. **§4 invariants 5 and 9:** trigger-incremented `version`/`revision`; one `challenge_reward` per challenge.
6. **§4 checks:** `handle_normalized = handle`; ledger key shape per reason; `equipped_json` shape.
7. **§2 J4:** player deletion is anonymization with a `d_` pseudonym handle (prefix reserved).
8. **§4 second schema review:** no FK on `challenges.winner_player_id` (deadlock); player locks use `FOR NO KEY UPDATE`; idempotency lease only while `in_progress` and a fenced completion; composite FKs tie challenge runs to their player, ruleset and challenge, and purchase grants to the buyer's ledger row for that item; a recipient run cannot create a challenge; derived-field CHECKs (`reward_day`, `auth_subject` by kind, `deleted_at`, export status fields); audit-log retention deletes only rows older than 90 days; the finished-session guard protects a fixed column list.

### v1.4 (2026-11-07) — signed §17 decisions

1. **§2 J1, §11 age (decision 1):** VYBE is for adults (18+ or the local age of majority). A neutral age gate (self-declared birth year, no hint of the accepted answer) comes before `POST /guests`. An under-age answer creates no player and no cookie. The API stores only the gate result (`age_confirmed_at`, policy version, self-declared band), never the birth date. Linking (J3) keeps the result. Countries: the counsel-approved list only.
2. **§2 J1, §4 `guest_sessions` (decision 2):** a guest session expires 30 days after its last use, renewed at most once per UTC day, and never later than 90 days after the guest player was created. `GET /me` returns `guestExpiresAt` for guests. The hub shows a link prompt when fewer than 14 days remain. A guest player whose last guest session expired 30 days ago is anonymized like J4. Support never transfers guest progress.
3. **§9 art (decision 3):** catalog items, bodies and emotes come from one contractor or studio under work-for-hire with full rights assignment; licensed packs only for neutral props with a license record; no AI-generated catalog items without counsel's written view. The team owns the manifest pipeline.
4. **§7 rewards (decision 4):** new ruleset config `minRunMsForRewards` (v1: `durationMs − earlyToleranceMs` = 58000). A run is reward-eligible only when it also has `submittedAt − startedAt ≥ minRunMsForRewards`; a shorter verified run gets 0 coins, 0 XP, no quest progress and no challenge eligibility. Base coins need `score ≥ 500` (`minScoreForCoins`). Other v1 values, `start_deadline` 10 min, quest claim window 7 days and report limit 10/day stay. The server writes `review_flags` with the starting thresholds of decision 4 (flags only, no automatic action).
5. **§8 economy (decision 5):** earn formulas unchanged. Price bands: common 300–800, rare 1,500–3,000, epic 6,000–10,000; at least 30 priced items at beta. No time-limited items in MVP.
6. **§3, §5 profiles (decision 6):** `GET /profiles/{handle}` answers only signed-in callers (guest or registered); anonymous callers get the same 404 as an unknown handle. A player can set the profile to "private" in `/settings`; a private profile answers 404 to everybody except its owner. Profile responses carry `X-Robots-Tag: noindex` and `Cache-Control: private, no-store`.
7. **§13 hosting (decision 7):** Supabase Pro (PostgreSQL + Auth) and one always-on API container on Fly.io in the same region (a second instance before the social beta), API through the transaction pooler, `/api` and the web app on one origin behind a CDN; beta budget ceiling USD 150 per month; named primary and backup on-call in business hours.
8. **§2 J4, §11 retention (decision 8):** the pseudonymized ledger stays without a time limit if counsel accepts it (otherwise a fixed period). J4 also deletes the Supabase Auth user. Support: email and web form, response target 3 business days. Notices say "anonymize", state the backup window and never promise immediate total erasure.

---

**This is a detailed implementation baseline, not executable software or a verified production-ready specification.** Before sprint implementation, convert each API endpoint into OpenAPI YAML, each table into EF Core migrations, each state machine into executable tests, and game rules into deterministic shared fixtures. Resolve sign-off decisions before public launch.
