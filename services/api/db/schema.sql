DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'vybe') THEN
        CREATE SCHEMA vybe;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS vybe.__ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'vybe') THEN
        CREATE SCHEMA vybe;
    END IF;
END $EF$;

CREATE TABLE vybe.admin_audit_logs (
    id uuid NOT NULL,
    actor_subject text NOT NULL,
    actor_role text NOT NULL,
    action text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    before_json jsonb,
    after_json jsonb,
    reason text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_admin_audit_logs PRIMARY KEY (id),
    CONSTRAINT ck_admin_audit_logs_actor_role CHECK (actor_role IN ('administrator', 'moderator')),
    CONSTRAINT ck_admin_audit_logs_reason_length CHECK (length(reason) BETWEEN 3 AND 500)
);

CREATE TABLE vybe.catalog_items (
    id uuid NOT NULL,
    sku text NOT NULL,
    slot text NOT NULL,
    name text NOT NULL,
    price bigint NOT NULL,
    min_level integer NOT NULL DEFAULT 1,
    asset_key text NOT NULL,
    compatible_body_presets text[] NOT NULL,
    rarity text NOT NULL,
    is_active boolean NOT NULL,
    catalog_version integer NOT NULL,
    available_from timestamp with time zone,
    available_until timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_catalog_items PRIMARY KEY (id),
    CONSTRAINT ck_catalog_items_availability_window CHECK (available_from IS NULL OR available_until IS NULL OR available_until > available_from),
    CONSTRAINT ck_catalog_items_catalog_version CHECK (catalog_version >= 1),
    CONSTRAINT ck_catalog_items_compatible_body_presets CHECK (cardinality(compatible_body_presets) >= 1),
    CONSTRAINT ck_catalog_items_min_level CHECK (min_level BETWEEN 1 AND 50),
    CONSTRAINT ck_catalog_items_price CHECK (price >= 0),
    CONSTRAINT ck_catalog_items_rarity CHECK (rarity IN ('common', 'epic', 'rare')),
    CONSTRAINT ck_catalog_items_slot CHECK (slot IN ('accessory', 'bottom', 'emote', 'hair', 'shoes', 'top'))
);

CREATE TABLE vybe.deletion_requests (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    completed_at timestamp with time zone,
    CONSTRAINT pk_deletion_requests PRIMARY KEY (id),
    CONSTRAINT ck_deletion_requests_status CHECK (status IN ('completed', 'processing', 'queued'))
);

CREATE TABLE vybe.game_rulesets (
    id text NOT NULL,
    game_id text NOT NULL,
    version integer NOT NULL,
    config jsonb NOT NULL,
    checksum text NOT NULL,
    active_from timestamp with time zone NOT NULL,
    retired_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_game_rulesets PRIMARY KEY (id),
    CONSTRAINT ck_game_rulesets_checksum_format CHECK (checksum ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_game_rulesets_config_object CHECK (jsonb_typeof(config) = 'object'),
    CONSTRAINT ck_game_rulesets_version CHECK (version >= 1)
);

CREATE TABLE vybe.idempotency_records (
    actor_subject text NOT NULL,
    operation text NOT NULL,
    key uuid NOT NULL,
    request_hash text NOT NULL,
    status text NOT NULL,
    locked_until timestamp with time zone,
    response_status integer,
    response_body jsonb,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    expires_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_idempotency_records PRIMARY KEY (actor_subject, operation, key),
    CONSTRAINT ck_idempotency_records_completed_has_response CHECK (status <> 'completed' OR response_status IS NOT NULL),
    CONSTRAINT ck_idempotency_records_lease_only_in_progress CHECK ((status = 'in_progress') = (locked_until IS NOT NULL)),
    CONSTRAINT ck_idempotency_records_response_status CHECK (response_status IS NULL OR response_status BETWEEN 100 AND 599),
    CONSTRAINT ck_idempotency_records_status CHECK (status IN ('completed', 'in_progress'))
);

CREATE TABLE vybe.outbox_jobs (
    id uuid NOT NULL,
    kind text NOT NULL,
    job_key text NOT NULL,
    payload jsonb NOT NULL,
    status text NOT NULL,
    attempts integer NOT NULL DEFAULT 0,
    run_after timestamp with time zone NOT NULL,
    last_error text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_outbox_jobs PRIMARY KEY (id),
    CONSTRAINT ck_outbox_jobs_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_outbox_jobs_payload_object CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT ck_outbox_jobs_status CHECK (status IN ('done', 'failed', 'pending'))
);

CREATE TABLE vybe.players (
    id uuid NOT NULL,
    auth_subject text,
    handle text NOT NULL,
    handle_normalized text NOT NULL,
    display_name character varying(40),
    kind text NOT NULL,
    status text NOT NULL,
    level integer NOT NULL DEFAULT 1,
    xp_total bigint NOT NULL DEFAULT 0,
    suspended_until timestamp with time zone,
    suspension_reason text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    deleted_at timestamp with time zone,
    CONSTRAINT pk_players PRIMARY KEY (id),
    CONSTRAINT ck_players_auth_subject_by_kind CHECK (status = 'deleted' OR (kind = 'guest') = (auth_subject IS NULL)),
    CONSTRAINT ck_players_deleted_at CHECK ((status = 'deleted') = (deleted_at IS NOT NULL)),
    CONSTRAINT ck_players_deleted_pseudonym CHECK ((status = 'deleted') = (handle ~ '^d_[a-z0-9]{18}$')),
    CONSTRAINT ck_players_handle_format CHECK (handle ~ '^[a-z0-9_]{3,20}$'),
    CONSTRAINT ck_players_handle_normalized CHECK (handle_normalized = handle),
    CONSTRAINT ck_players_kind CHECK (kind IN ('guest', 'registered')),
    CONSTRAINT ck_players_level CHECK (level BETWEEN 1 AND 50),
    CONSTRAINT ck_players_status CHECK (status IN ('active', 'deleted', 'deleting', 'suspended')),
    CONSTRAINT ck_players_xp_total CHECK (xp_total >= 0)
);

CREATE TABLE vybe.quest_definitions (
    id uuid NOT NULL,
    code text NOT NULL,
    version integer NOT NULL,
    period text NOT NULL,
    criteria jsonb NOT NULL,
    target integer NOT NULL,
    reward_coins bigint NOT NULL,
    reward_xp integer NOT NULL,
    is_active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_quest_definitions PRIMARY KEY (id),
    CONSTRAINT ck_quest_definitions_criteria_object CHECK (jsonb_typeof(criteria) = 'object'),
    CONSTRAINT ck_quest_definitions_period CHECK (period IN ('daily', 'once')),
    CONSTRAINT ck_quest_definitions_rewards CHECK (reward_coins >= 0 AND reward_xp >= 0),
    CONSTRAINT ck_quest_definitions_target CHECK (target > 0)
);

CREATE TABLE vybe.achievements (
    id uuid NOT NULL,
    code text NOT NULL,
    criteria jsonb NOT NULL,
    reward_item_id uuid,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_achievements PRIMARY KEY (id),
    CONSTRAINT ck_achievements_criteria_object CHECK (jsonb_typeof(criteria) = 'object'),
    CONSTRAINT fk_achievements_catalog_items_reward_item_id FOREIGN KEY (reward_item_id) REFERENCES vybe.catalog_items (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.avatar_configs (
    player_id uuid NOT NULL,
    body_preset_id text NOT NULL,
    skin_tone_id text NOT NULL,
    face_preset_id text NOT NULL,
    hair_preset_id text NOT NULL,
    hair_color_id text NOT NULL,
    equipped_json jsonb NOT NULL,
    revision bigint NOT NULL DEFAULT 0,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_avatar_configs PRIMARY KEY (player_id),
    CONSTRAINT ck_avatar_configs_equipped_json_shape CHECK (jsonb_typeof(equipped_json) = 'object' AND equipped_json ?& ARRAY['hair', 'top', 'bottom', 'shoes', 'emote', 'accessories'] AND CASE WHEN jsonb_typeof(equipped_json -> 'accessories') = 'array' THEN jsonb_array_length(equipped_json -> 'accessories') = 2 ELSE false END),
    CONSTRAINT ck_avatar_configs_revision CHECK (revision >= 0),
    CONSTRAINT fk_avatar_configs_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.data_export_requests (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    status text NOT NULL,
    storage_key text,
    download_expires_at timestamp with time zone,
    failure_code text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    completed_at timestamp with time zone,
    CONSTRAINT pk_data_export_requests PRIMARY KEY (id),
    CONSTRAINT ck_data_export_requests_failed_fields CHECK ((status = 'failed') = (failure_code IS NOT NULL)),
    CONSTRAINT ck_data_export_requests_ready_fields CHECK (status <> 'ready' OR (storage_key IS NOT NULL AND download_expires_at IS NOT NULL)),
    CONSTRAINT ck_data_export_requests_status CHECK (status IN ('failed', 'queued', 'ready', 'running')),
    CONSTRAINT fk_data_export_requests_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.guest_sessions (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    token_hash bytea NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_guest_sessions PRIMARY KEY (id),
    CONSTRAINT ck_guest_sessions_token_hash_sha256 CHECK (octet_length(token_hash) = 32),
    CONSTRAINT fk_guest_sessions_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_blocks (
    blocker_player_id uuid NOT NULL,
    blocked_player_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_player_blocks PRIMARY KEY (blocker_player_id, blocked_player_id),
    CONSTRAINT ck_player_blocks_not_self CHECK (blocker_player_id <> blocked_player_id),
    CONSTRAINT fk_player_blocks_players_blocked_player_id FOREIGN KEY (blocked_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_blocks_players_blocker_player_id FOREIGN KEY (blocker_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_daily_counters (
    player_id uuid NOT NULL,
    day date NOT NULL,
    runs_rewarded integer NOT NULL DEFAULT 0,
    xp_from_games integer NOT NULL DEFAULT 0,
    challenge_bonuses integer NOT NULL DEFAULT 0,
    CONSTRAINT pk_player_daily_counters PRIMARY KEY (player_id, day),
    CONSTRAINT ck_player_daily_counters_non_negative CHECK (runs_rewarded >= 0 AND xp_from_games >= 0 AND challenge_bonuses >= 0),
    CONSTRAINT fk_player_daily_counters_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_reports (
    id uuid NOT NULL,
    reporter_player_id uuid NOT NULL,
    target_player_id uuid NOT NULL,
    reason text NOT NULL,
    details character varying(500),
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    reviewed_at timestamp with time zone,
    reviewed_by text,
    CONSTRAINT pk_player_reports PRIMARY KEY (id),
    CONSTRAINT ck_player_reports_not_self CHECK (reporter_player_id <> target_player_id),
    CONSTRAINT ck_player_reports_reason CHECK (reason IN ('cheating', 'harassment', 'offensive_name', 'other')),
    CONSTRAINT ck_player_reports_status CHECK (status IN ('actioned', 'dismissed', 'open')),
    CONSTRAINT fk_player_reports_players_reporter_player_id FOREIGN KEY (reporter_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_reports_players_target_player_id FOREIGN KEY (target_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.saved_outfits (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    slot smallint NOT NULL,
    name character varying(40) NOT NULL,
    equipped_json jsonb NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_saved_outfits PRIMARY KEY (id),
    CONSTRAINT ck_saved_outfits_equipped_json_shape CHECK (jsonb_typeof(equipped_json) = 'object' AND equipped_json ?& ARRAY['hair', 'top', 'bottom', 'shoes', 'emote', 'accessories'] AND CASE WHEN jsonb_typeof(equipped_json -> 'accessories') = 'array' THEN jsonb_array_length(equipped_json -> 'accessories') = 2 ELSE false END),
    CONSTRAINT ck_saved_outfits_name_not_empty CHECK (length(name) >= 1),
    CONSTRAINT ck_saved_outfits_slot CHECK (slot BETWEEN 1 AND 3),
    CONSTRAINT fk_saved_outfits_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.wallet_transactions (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    delta bigint NOT NULL,
    balance_after bigint NOT NULL,
    reason text NOT NULL,
    reference_type text,
    reference_id uuid,
    ledger_key text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_wallet_transactions PRIMARY KEY (id),
    CONSTRAINT ck_wallet_transactions_balance_after CHECK (balance_after >= 0),
    CONSTRAINT ck_wallet_transactions_delta CHECK (delta <> 0),
    CONSTRAINT ck_wallet_transactions_ledger_key_not_empty CHECK (length(ledger_key) >= 1),
    CONSTRAINT ck_wallet_transactions_ledger_key_shape CHECK (CASE reason WHEN 'game_reward' THEN ledger_key = 'game:' || reference_id::text WHEN 'quest_reward' THEN ledger_key = 'quest:' || reference_id::text WHEN 'challenge_reward' THEN ledger_key = 'challenge:' || reference_id::text WHEN 'purchase' THEN ledger_key = 'purchase:' || reference_id::text WHEN 'level_reward' THEN ledger_key ~ '^level:([2-9]|[1-4][0-9]|50)$' WHEN 'admin_adjustment' THEN ledger_key ~ '^admin:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' ELSE false END),
    CONSTRAINT ck_wallet_transactions_reason CHECK (reason IN ('admin_adjustment', 'challenge_reward', 'game_reward', 'level_reward', 'purchase', 'quest_reward')),
    CONSTRAINT ck_wallet_transactions_reference_required CHECK ((reason = 'level_reward') = (reference_id IS NULL)),
    CONSTRAINT fk_wallet_transactions_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.wallets (
    player_id uuid NOT NULL,
    balance bigint NOT NULL DEFAULT 0,
    revision bigint NOT NULL DEFAULT 0,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_wallets PRIMARY KEY (player_id),
    CONSTRAINT ck_wallets_balance CHECK (balance >= 0),
    CONSTRAINT ck_wallets_revision CHECK (revision >= 0),
    CONSTRAINT fk_wallets_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_quests (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    quest_id uuid NOT NULL,
    period_start date NOT NULL,
    target integer NOT NULL,
    progress integer NOT NULL DEFAULT 0,
    status text NOT NULL,
    completed_at timestamp with time zone,
    claimed_at timestamp with time zone,
    CONSTRAINT pk_player_quests PRIMARY KEY (id),
    CONSTRAINT ck_player_quests_claimed_fields CHECK ((status = 'claimed') = (claimed_at IS NOT NULL)),
    CONSTRAINT ck_player_quests_completed_fields CHECK (status = 'active' OR (progress = target AND completed_at IS NOT NULL)),
    CONSTRAINT ck_player_quests_progress CHECK (progress BETWEEN 0 AND target),
    CONSTRAINT ck_player_quests_status CHECK (status IN ('active', 'claimed', 'completed')),
    CONSTRAINT ck_player_quests_target CHECK (target > 0),
    CONSTRAINT fk_player_quests_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_quests_quest_definitions_quest_id FOREIGN KEY (quest_id) REFERENCES vybe.quest_definitions (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_achievements (
    player_id uuid NOT NULL,
    achievement_id uuid NOT NULL,
    unlocked_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_player_achievements PRIMARY KEY (player_id, achievement_id),
    CONSTRAINT fk_player_achievements_achievements_achievement_id FOREIGN KEY (achievement_id) REFERENCES vybe.achievements (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_achievements_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.player_inventory (
    player_id uuid NOT NULL,
    item_id uuid NOT NULL,
    acquired_at timestamp with time zone NOT NULL DEFAULT (now()),
    source text NOT NULL,
    source_transaction_id uuid,
    source_reference_id uuid,
    CONSTRAINT pk_player_inventory PRIMARY KEY (player_id, item_id),
    CONSTRAINT ck_player_inventory_purchase_has_transaction CHECK ((source = 'purchase') = (source_transaction_id IS NOT NULL)),
    CONSTRAINT ck_player_inventory_source CHECK (source IN ('achievement_grant', 'admin_grant', 'purchase', 'starter_grant')),
    CONSTRAINT fk_player_inventory_catalog_items_item_id FOREIGN KEY (item_id) REFERENCES vybe.catalog_items (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_inventory_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT,
    CONSTRAINT fk_player_inventory_wallet_transactions_source_transaction_id FOREIGN KEY (source_transaction_id) REFERENCES vybe.wallet_transactions (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.challenges (
    id uuid NOT NULL,
    creator_player_id uuid NOT NULL,
    creator_session_id uuid NOT NULL,
    recipient_player_id uuid,
    invite_token_hash bytea NOT NULL,
    ruleset_id text NOT NULL,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    accept_deadline timestamp with time zone NOT NULL,
    accepted_at timestamp with time zone,
    complete_deadline timestamp with time zone,
    recipient_session_id uuid,
    winner_player_id uuid,
    rematch_of_challenge_id uuid,
    suppressed boolean NOT NULL DEFAULT FALSE,
    resolved_at timestamp with time zone,
    version bigint NOT NULL DEFAULT 0,
    CONSTRAINT pk_challenges PRIMARY KEY (id),
    CONSTRAINT ck_challenges_accepted_fields CHECK (status NOT IN ('accepted', 'completed') OR (recipient_player_id IS NOT NULL AND accepted_at IS NOT NULL AND complete_deadline IS NOT NULL)),
    CONSTRAINT ck_challenges_completed_fields CHECK (status <> 'completed' OR (recipient_session_id IS NOT NULL AND resolved_at IS NOT NULL)),
    CONSTRAINT ck_challenges_distinct_sessions CHECK (recipient_session_id IS NULL OR recipient_session_id <> creator_session_id),
    CONSTRAINT ck_challenges_not_self CHECK (recipient_player_id IS NULL OR recipient_player_id <> creator_player_id),
    CONSTRAINT ck_challenges_status CHECK (status IN ('accepted', 'cancelled', 'completed', 'expired', 'open')),
    CONSTRAINT ck_challenges_token_hash_sha256 CHECK (octet_length(invite_token_hash) = 32),
    CONSTRAINT ck_challenges_winner_is_participant CHECK (winner_player_id IS NULL OR winner_player_id = creator_player_id OR winner_player_id = recipient_player_id),
    CONSTRAINT ck_challenges_winner_only_when_completed CHECK (winner_player_id IS NULL OR status = 'completed'),
    CONSTRAINT fk_challenges_challenges_rematch_of_challenge_id FOREIGN KEY (rematch_of_challenge_id) REFERENCES vybe.challenges (id) ON DELETE RESTRICT,
    CONSTRAINT fk_challenges_game_rulesets_ruleset_id FOREIGN KEY (ruleset_id) REFERENCES vybe.game_rulesets (id) ON DELETE RESTRICT,
    CONSTRAINT fk_challenges_players_creator_player_id FOREIGN KEY (creator_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT,
    CONSTRAINT fk_challenges_players_recipient_player_id FOREIGN KEY (recipient_player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.game_sessions (
    id uuid NOT NULL,
    player_id uuid NOT NULL,
    game_id text NOT NULL,
    ruleset_id text NOT NULL,
    seed bigint NOT NULL,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    start_deadline timestamp with time zone NOT NULL,
    started_at timestamp with time zone,
    submit_deadline timestamp with time zone,
    submitted_at timestamp with time zone,
    verified_at timestamp with time zone,
    reward_day date,
    event_count integer,
    score integer,
    reward_coins bigint NOT NULL DEFAULT 0,
    reward_xp integer NOT NULL DEFAULT 0,
    input_trace_hash text,
    rejection_reason text,
    review_flags text[] NOT NULL DEFAULT ('{}'::text[]),
    challenge_id uuid,
    version bigint NOT NULL DEFAULT 0,
    CONSTRAINT pk_game_sessions PRIMARY KEY (id),
    CONSTRAINT ak_game_sessions_id_player_id_ruleset_id UNIQUE (id, player_id, ruleset_id),
    CONSTRAINT ck_game_sessions_non_negative CHECK (COALESCE(score, 0) >= 0 AND COALESCE(event_count, 0) >= 0 AND reward_coins >= 0 AND reward_xp >= 0),
    CONSTRAINT ck_game_sessions_rejected_has_reason CHECK ((status = 'rejected') = (rejection_reason IS NOT NULL)),
    CONSTRAINT ck_game_sessions_rejection_reason CHECK (rejection_reason IN ('cadence_violation', 'invalid_event', 'non_monotonic', 'not_started', 'out_of_range', 'ruleset_mismatch', 'submitted_too_early', 'too_many_events')),
    CONSTRAINT ck_game_sessions_reward_day_utc CHECK (reward_day IS NULL OR reward_day = (started_at AT TIME ZONE 'UTC')::date),
    CONSTRAINT ck_game_sessions_seed_uint32 CHECK (seed BETWEEN 0 AND 4294967295),
    CONSTRAINT ck_game_sessions_started_fields CHECK (status NOT IN ('active', 'submitted', 'verified') OR (started_at IS NOT NULL AND submit_deadline IS NOT NULL)),
    CONSTRAINT ck_game_sessions_status CHECK (status IN ('active', 'created', 'expired', 'rejected', 'submitted', 'verified')),
    CONSTRAINT ck_game_sessions_verified_fields CHECK (status <> 'verified' OR (score IS NOT NULL AND verified_at IS NOT NULL AND reward_day IS NOT NULL)),
    CONSTRAINT fk_game_sessions_challenges_challenge_id FOREIGN KEY (challenge_id) REFERENCES vybe.challenges (id) ON DELETE RESTRICT,
    CONSTRAINT fk_game_sessions_game_rulesets_ruleset_id FOREIGN KEY (ruleset_id) REFERENCES vybe.game_rulesets (id) ON DELETE RESTRICT,
    CONSTRAINT fk_game_sessions_players_player_id FOREIGN KEY (player_id) REFERENCES vybe.players (id) ON DELETE RESTRICT
);

CREATE TABLE vybe.game_input_traces (
    session_id uuid NOT NULL,
    compressed_trace bytea NOT NULL,
    encoding text NOT NULL,
    event_count integer NOT NULL,
    received_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_game_input_traces PRIMARY KEY (session_id),
    CONSTRAINT ck_game_input_traces_event_count CHECK (event_count >= 0),
    CONSTRAINT ck_game_input_traces_size_cap CHECK (octet_length(compressed_trace) <= 16384),
    CONSTRAINT fk_game_input_traces_game_sessions_session_id FOREIGN KEY (session_id) REFERENCES vybe.game_sessions (id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ix_achievements_code ON vybe.achievements (code);

CREATE INDEX ix_achievements_reward_item_id ON vybe.achievements (reward_item_id);

CREATE INDEX ix_admin_audit_logs_actor_subject_created_at ON vybe.admin_audit_logs (actor_subject, created_at DESC);

CREATE INDEX ix_admin_audit_logs_created_at ON vybe.admin_audit_logs (created_at);

CREATE INDEX ix_admin_audit_logs_entity_type_entity_id_created_at ON vybe.admin_audit_logs (entity_type, entity_id, created_at DESC);

CREATE UNIQUE INDEX ix_catalog_items_sku ON vybe.catalog_items (sku);

CREATE INDEX ix_challenges_accepted_complete_deadline ON vybe.challenges (complete_deadline) WHERE status = 'accepted';

CREATE INDEX ix_challenges_creator_player_id_created_at ON vybe.challenges (creator_player_id, created_at DESC);

CREATE UNIQUE INDEX ix_challenges_creator_session_id ON vybe.challenges (creator_session_id);

CREATE INDEX ix_challenges_creator_session_id_creator_player_id_ruleset_id ON vybe.challenges (creator_session_id, creator_player_id, ruleset_id);

CREATE UNIQUE INDEX ix_challenges_invite_token_hash ON vybe.challenges (invite_token_hash);

CREATE INDEX ix_challenges_recipient_player_id_status ON vybe.challenges (recipient_player_id, status);

CREATE UNIQUE INDEX ix_challenges_recipient_session_id ON vybe.challenges (recipient_session_id) WHERE recipient_session_id IS NOT NULL;

CREATE INDEX ix_challenges_recipient_session_id_recipient_player_id_ruleset ON vybe.challenges (recipient_session_id, recipient_player_id, ruleset_id);

CREATE UNIQUE INDEX ix_challenges_rematch_of_challenge_id_creator_player_id ON vybe.challenges (rematch_of_challenge_id, creator_player_id) WHERE rematch_of_challenge_id IS NOT NULL;

CREATE INDEX ix_challenges_ruleset_id ON vybe.challenges (ruleset_id);

CREATE INDEX ix_challenges_status_accept_deadline ON vybe.challenges (status, accept_deadline);

CREATE INDEX ix_challenges_winner_player_id ON vybe.challenges (winner_player_id);

CREATE INDEX ix_data_export_requests_player_id_created_at ON vybe.data_export_requests (player_id, created_at DESC);

CREATE UNIQUE INDEX ux_data_export_requests_one_pending_per_player ON vybe.data_export_requests (player_id) WHERE status IN ('queued', 'running');

CREATE INDEX ix_deletion_requests_player_id ON vybe.deletion_requests (player_id);

CREATE INDEX ix_game_input_traces_received_at ON vybe.game_input_traces (received_at);

CREATE UNIQUE INDEX ix_game_rulesets_game_id_version ON vybe.game_rulesets (game_id, version);

CREATE INDEX ix_game_sessions_active_submit_deadline ON vybe.game_sessions (submit_deadline) WHERE status = 'active';

CREATE UNIQUE INDEX ix_game_sessions_challenge_id ON vybe.game_sessions (challenge_id) WHERE challenge_id IS NOT NULL;

CREATE INDEX ix_game_sessions_created_start_deadline ON vybe.game_sessions (start_deadline) WHERE status = 'created';

CREATE INDEX ix_game_sessions_player_id_created_at ON vybe.game_sessions (player_id, created_at DESC);

CREATE INDEX ix_game_sessions_ruleset_id ON vybe.game_sessions (ruleset_id);

CREATE UNIQUE INDEX ux_game_sessions_one_open_per_player ON vybe.game_sessions (player_id) WHERE status IN ('created', 'active');

CREATE INDEX ix_guest_sessions_player_id ON vybe.guest_sessions (player_id);

CREATE UNIQUE INDEX ix_guest_sessions_token_hash ON vybe.guest_sessions (token_hash);

CREATE INDEX ix_idempotency_records_expires_at ON vybe.idempotency_records (expires_at);

CREATE UNIQUE INDEX ix_outbox_jobs_job_key ON vybe.outbox_jobs (job_key);

CREATE INDEX ix_outbox_jobs_status_run_after ON vybe.outbox_jobs (status, run_after);

CREATE INDEX ix_player_achievements_achievement_id ON vybe.player_achievements (achievement_id);

CREATE INDEX ix_player_blocks_blocked_player_id ON vybe.player_blocks (blocked_player_id);

CREATE INDEX ix_player_inventory_item_id ON vybe.player_inventory (item_id);

CREATE UNIQUE INDEX ix_player_inventory_source_transaction_id ON vybe.player_inventory (source_transaction_id) WHERE source_transaction_id IS NOT NULL;

CREATE UNIQUE INDEX ix_player_quests_player_id_quest_id_period_start ON vybe.player_quests (player_id, quest_id, period_start);

CREATE INDEX ix_player_quests_quest_id ON vybe.player_quests (quest_id);

CREATE INDEX ix_player_reports_reporter_player_id ON vybe.player_reports (reporter_player_id);

CREATE INDEX ix_player_reports_status_created_at ON vybe.player_reports (status, created_at);

CREATE INDEX ix_player_reports_target_player_id ON vybe.player_reports (target_player_id);

CREATE UNIQUE INDEX ix_players_auth_subject ON vybe.players (auth_subject);

CREATE UNIQUE INDEX ix_players_handle_normalized ON vybe.players (handle_normalized);

CREATE INDEX ix_players_suspended_until ON vybe.players (suspended_until) WHERE status = 'suspended';

CREATE UNIQUE INDEX ix_quest_definitions_code_version ON vybe.quest_definitions (code, version);

CREATE UNIQUE INDEX ix_saved_outfits_player_id_slot ON vybe.saved_outfits (player_id, slot);

CREATE INDEX ix_wallet_transactions_player_id_created_at ON vybe.wallet_transactions (player_id, created_at DESC);

CREATE UNIQUE INDEX ix_wallet_transactions_player_id_ledger_key ON vybe.wallet_transactions (player_id, ledger_key);

CREATE INDEX ix_wallet_transactions_reason_reference_id ON vybe.wallet_transactions (reason, reference_id);

CREATE UNIQUE INDEX ux_wallet_transactions_one_challenge_reward ON vybe.wallet_transactions (reference_id) WHERE reason = 'challenge_reward';

ALTER TABLE vybe.challenges ADD CONSTRAINT fk_challenges_creator_session FOREIGN KEY (creator_session_id, creator_player_id, ruleset_id) REFERENCES vybe.game_sessions (id, player_id, ruleset_id) ON DELETE RESTRICT;

ALTER TABLE vybe.challenges ADD CONSTRAINT fk_challenges_recipient_session FOREIGN KEY (recipient_session_id, recipient_player_id, ruleset_id) REFERENCES vybe.game_sessions (id, player_id, ruleset_id) ON DELETE RESTRICT;

INSERT INTO vybe.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106122820_InitialSchema', '10.0.13');

COMMIT;

START TRANSACTION;
-- Append-only. The only exception: the audit-log retention job (spec §11, 90 days)
-- deletes rows older than 90 days inside a transaction that ran
-- set_config('vybe.retention_delete', 'on', true). The age check makes a leaked
-- session-level setting (e.g. on a pooled connection) unable to delete recent rows.
CREATE FUNCTION vybe.reject_append_only_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND TG_LEVEL = 'ROW' AND TG_TABLE_NAME = 'admin_audit_logs'
       AND current_setting('vybe.retention_delete', true) = 'on'
       AND OLD.created_at < now() - interval '90 days' THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'table %.% is append-only (% rejected)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;

CREATE TRIGGER wallet_transactions_append_only
    BEFORE UPDATE OR DELETE ON vybe.wallet_transactions
    FOR EACH ROW EXECUTE FUNCTION vybe.reject_append_only_change();
CREATE TRIGGER wallet_transactions_no_truncate
    BEFORE TRUNCATE ON vybe.wallet_transactions
    FOR EACH STATEMENT EXECUTE FUNCTION vybe.reject_append_only_change();

CREATE TRIGGER admin_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON vybe.admin_audit_logs
    FOR EACH ROW EXECUTE FUNCTION vybe.reject_append_only_change();
CREATE TRIGGER admin_audit_logs_no_truncate
    BEFORE TRUNCATE ON vybe.admin_audit_logs
    FOR EACH STATEMENT EXECUTE FUNCTION vybe.reject_append_only_change();

-- Rulesets are immutable; only retirement may be recorded.
CREATE FUNCTION vybe.guard_game_ruleset() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'game ruleset % cannot be deleted', OLD.id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF (to_jsonb(NEW) - 'retired_at') IS DISTINCT FROM (to_jsonb(OLD) - 'retired_at') THEN
        RAISE EXCEPTION 'game ruleset % is immutable; only retired_at may change', OLD.id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.retired_at IS NOT NULL AND NEW.retired_at IS DISTINCT FROM OLD.retired_at THEN
        RAISE EXCEPTION 'game ruleset % is already retired', OLD.id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER game_rulesets_immutable
    BEFORE UPDATE OR DELETE ON vybe.game_rulesets
    FOR EACH ROW EXECUTE FUNCTION vybe.guard_game_ruleset();

-- Finished sessions: the outcome columns are immutable. A fixed column list (not the
-- whole row) lets future expand/contract migrations backfill new columns; a new
-- column that must also be immutable is added to this list in its migration.
CREATE FUNCTION vybe.guard_finished_game_session() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('verified', 'rejected', 'expired')
       AND ROW(NEW.player_id, NEW.game_id, NEW.ruleset_id, NEW.seed, NEW.status, NEW.created_at,
               NEW.start_deadline, NEW.started_at, NEW.submit_deadline, NEW.submitted_at,
               NEW.verified_at, NEW.reward_day, NEW.event_count, NEW.score, NEW.reward_coins,
               NEW.reward_xp, NEW.input_trace_hash, NEW.rejection_reason, NEW.challenge_id)
           IS DISTINCT FROM
           ROW(OLD.player_id, OLD.game_id, OLD.ruleset_id, OLD.seed, OLD.status, OLD.created_at,
               OLD.start_deadline, OLD.started_at, OLD.submit_deadline, OLD.submitted_at,
               OLD.verified_at, OLD.reward_day, OLD.event_count, OLD.score, OLD.reward_coins,
               OLD.reward_xp, OLD.input_trace_hash, OLD.rejection_reason, OLD.challenge_id) THEN
        RAISE EXCEPTION 'game session % is %; its outcome is immutable', OLD.id, OLD.status
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER game_sessions_finished_immutable
    BEFORE UPDATE ON vybe.game_sessions
    FOR EACH ROW EXECUTE FUNCTION vybe.guard_finished_game_session();

-- Concurrency tokens (spec §4 invariant 5): every UPDATE increments them, so a
-- compare-and-swap on version/revision cannot pass twice, even if code forgets to bump.
-- Trigger names sort before the guards, so the guards see the incremented value.
CREATE FUNCTION vybe.increment_version() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.version := OLD.version + 1;
    RETURN NEW;
END;
$$;

CREATE FUNCTION vybe.increment_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.revision := OLD.revision + 1;
    RETURN NEW;
END;
$$;

CREATE TRIGGER game_sessions_a_increment_version
    BEFORE UPDATE ON vybe.game_sessions
    FOR EACH ROW EXECUTE FUNCTION vybe.increment_version();
CREATE TRIGGER challenges_a_increment_version
    BEFORE UPDATE ON vybe.challenges
    FOR EACH ROW EXECUTE FUNCTION vybe.increment_version();
CREATE TRIGGER wallets_a_increment_revision
    BEFORE UPDATE ON vybe.wallets
    FOR EACH ROW EXECUTE FUNCTION vybe.increment_revision();
CREATE TRIGGER avatar_configs_a_increment_revision
    BEFORE UPDATE ON vybe.avatar_configs
    FOR EACH ROW EXECUTE FUNCTION vybe.increment_revision();

-- Foreign keys EF cannot model (their target columns are nullable).
-- The recipient session of a challenge carries that challenge's id.
ALTER TABLE vybe.game_sessions
    ADD CONSTRAINT ak_game_sessions_id_challenge_id UNIQUE (id, challenge_id);
ALTER TABLE vybe.challenges
    ADD CONSTRAINT fk_challenges_recipient_session_challenge
    FOREIGN KEY (recipient_session_id, id) REFERENCES vybe.game_sessions (id, challenge_id);
-- A purchase grant points at the buyer's own ledger row for the same item.
ALTER TABLE vybe.wallet_transactions
    ADD CONSTRAINT ak_wallet_transactions_id_player_id_reference_id UNIQUE (id, player_id, reference_id);
ALTER TABLE vybe.player_inventory
    ADD CONSTRAINT fk_player_inventory_purchase_transaction
    FOREIGN KEY (source_transaction_id, player_id, item_id)
    REFERENCES vybe.wallet_transactions (id, player_id, reference_id);

-- A recipient run (challenge_id set) cannot be the creator run of a challenge.
CREATE FUNCTION vybe.guard_challenge_creator_session() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM vybe.game_sessions
               WHERE id = NEW.creator_session_id AND challenge_id IS NOT NULL) THEN
        RAISE EXCEPTION 'session % is a recipient run and cannot create a challenge', NEW.creator_session_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER challenges_creator_session_not_recipient_run
    BEFORE INSERT OR UPDATE OF creator_session_id ON vybe.challenges
    FOR EACH ROW EXECUTE FUNCTION vybe.guard_challenge_creator_session();

REVOKE ALL ON FUNCTION vybe.guard_challenge_creator_session() FROM PUBLIC;
REVOKE ALL ON FUNCTION vybe.increment_version() FROM PUBLIC;
REVOKE ALL ON FUNCTION vybe.increment_revision() FROM PUBLIC;
REVOKE ALL ON FUNCTION vybe.reject_append_only_change() FROM PUBLIC;
REVOKE ALL ON FUNCTION vybe.guard_game_ruleset() FROM PUBLIC;
REVOKE ALL ON FUNCTION vybe.guard_finished_game_session() FROM PUBLIC;

-- Only the API's own role uses this schema. On Supabase, the Data API roles
-- (anon, authenticated) must never reach it.
REVOKE ALL ON SCHEMA vybe FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM PUBLIC;
DO $$
DECLARE
    api_role text;
BEGIN
    FOREACH api_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = api_role) THEN
            EXECUTE format('REVOKE ALL ON SCHEMA vybe FROM %I', api_role);
            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM %I', api_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM %I', api_role);
        END IF;
    END LOOP;
END;
$$;

INSERT INTO vybe.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106122824_DatabaseGuards', '10.0.13');

COMMIT;

START TRANSACTION;
INSERT INTO vybe.game_rulesets (id, game_id, version, config, checksum, active_from)
VALUES (
    'street-hoops-v1',
    'street-hoops',
    1,
    '{"streamId": 54, "durationMs": 60000, "graceMs": 5000, "earlyToleranceMs": 2000,
      "segmentMs": 4000, "xMin": -700, "xMax": 700, "pMin": 300, "pMax": 900,
      "hitDx": 160, "hitDp": 120, "perfectDx": 50, "perfectDp": 50,
      "hitPoints": 100, "perfectPoints": 150, "streakStart": 3, "streakStep": 25,
      "streakMax": 100, "minGapMs": 650, "maxEvents": 93, "minShotsForRewards": 5}',
    '9b3bd19baa803520ab49a48f96f415f477fdfedbfc5a842dbacf00cfc8f2948d',
    TIMESTAMPTZ '2026-11-06 00:00:00+00');

INSERT INTO vybe.__ef_migrations_history (migration_id, product_version)
VALUES ('20261106134720_SeedStreetHoopsV1', '10.0.13');

COMMIT;

